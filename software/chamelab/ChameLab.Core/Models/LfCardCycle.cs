using System;
using System.Linq;
using ChameLab.Protocol;

namespace ChameLab.Models;

/// <summary>A card the LF side cycles through: an emulated LF slot type and its emulator data.</summary>
public sealed record LfCycleCard(TagSpecificType Type, byte[] Id)
{
    /// <summary>Slot types whose card can cycle with others: every emulated LF type.</summary>
    public static bool CanCycle(TagSpecificType slotType) =>
        ChameleonClient.SlotTypeFor(slotType) == slotType && slotType.LfIdLength() > 0;

    public bool IsValid => CanCycle(Type) && Id.Length == Type.LfIdLength();

    public bool SameAs(LfCycleCard other) => Type == other.Type && Id.AsSpan().SequenceEqual(other.Id);
}

/// <summary>
/// Additional LF cards of any emulated type; the slot's own card is always first. The device
/// emulates the next card in the list on each field appearance, so there is no per-card timing.
/// </summary>
public sealed record LfCardCycle(LfCycleCard[] Extras)
{
    /// <summary>Largest LF emulator data; each card is zero-padded to it on the wire.</summary>
    private const int MaxIdLength = 16;
    /// <summary>One extra card on the wire: tag type (u16, big-endian) and 16 data bytes.</summary>
    public const int EntrySize = 2 + MaxIdLength;
    private const int MaxExtras = 7;

    public byte[] Encode()
    {
        if (Extras.Length > MaxExtras || Extras.Any(c => !c.IsValid))
            throw new ArgumentException("Invalid LF card cycle.");
        var data = new byte[1 + EntrySize * Extras.Length];
        data[0] = (byte)Extras.Length;
        for (var i = 0; i < Extras.Length; i++)
        {
            var o = 1 + EntrySize * i;
            data[o] = (byte)((ushort)Extras[i].Type >> 8);
            data[o + 1] = (byte)Extras[i].Type;
            Extras[i].Id.CopyTo(data, o + 2);
        }
        return data;
    }

    public static LfCardCycle Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1 || data[0] > MaxExtras || data.Length != 1 + EntrySize * data[0])
            throw new ArgumentException("Invalid LF card cycle response.");
        var extras = new LfCycleCard[data[0]];
        for (var i = 0; i < extras.Length; i++)
        {
            var entry = data.Slice(1 + EntrySize * i, EntrySize);
            var type = (TagSpecificType)((entry[0] << 8) | entry[1]);
            if (!LfCycleCard.CanCycle(type)) throw new ArgumentException("Invalid LF card cycle response.");
            extras[i] = new LfCycleCard(type, entry.Slice(2, type.LfIdLength()).ToArray());
        }
        return new LfCardCycle(extras);
    }

    /// <summary>Size of one slot's entry in the all-slots reply (type, own card, cycle zero-padded to seven cards).</summary>
    public const int AllCyclesEntrySize = 2 + MaxIdLength + 1 + EntrySize * MaxExtras;

    /// <summary>Decodes the all-slots reply: one entry per slot, null where the slot has no emulated LF card.</summary>
    public static LfCardCycle?[] DecodeAll(byte[] data)
    {
        if (data.Length == 0 || data.Length % AllCyclesEntrySize != 0)
            throw new ArgumentException("Invalid LF card cycles response.");
        var result = new LfCardCycle?[data.Length / AllCyclesEntrySize];
        for (var slot = 0; slot < result.Length; slot++)
        {
            var e = data.AsSpan(slot * AllCyclesEntrySize, AllCyclesEntrySize);
            if (e[0] == 0 && e[1] == 0) continue;
            var count = e[2 + MaxIdLength];
            if (count > MaxExtras) throw new ArgumentException("Invalid LF card cycles response.");
            result[slot] = Decode(e.Slice(2 + MaxIdLength, 1 + EntrySize * count));
        }
        return result;
    }

    /// <summary>The same cards in the same order (records compare the card arrays by reference).</summary>
    public bool SameAs(LfCardCycle other) =>
        Extras.Length == other.Extras.Length && Extras.Zip(other.Extras).All(p => p.First.SameAs(p.Second));
}
