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

/// <summary>How the device switches between the cards of an LF list.</summary>
public enum LfCycleMode : byte
{
    /// <summary>The next card on each appearance of the reader's field: one steady card per tap.</summary>
    PerField = 0,
    /// <summary>The cards in turn while the field is present, resuming from the last one sent.</summary>
    WhilePresent = 1,
}

/// <summary>
/// Additional LF cards of any emulated type; the slot's own card is always first. How the cards
/// are switched is <see cref="Mode"/>; <see cref="Bursts"/> per card and <see cref="GapSteps"/>
/// (100 ms) of silence between cards apply to <see cref="LfCycleMode.WhilePresent"/>.
/// </summary>
public sealed record LfCardCycle(LfCycleCard[] Extras)
{
    public LfCycleMode Mode { get; init; }
    public int Bursts { get; init; } = 1;
    public int GapSteps { get; init; }

    public const int MaxBursts = 10;
    public const int MaxGapSteps = 30;
    /// <summary>One slot's mode on the wire: mode, bursts, silence.</summary>
    public const int ModeSize = 3;

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

    /// <summary>The mode command payload (5910).</summary>
    public byte[] EncodeMode()
    {
        if (!Enum.IsDefined(Mode) || Bursts is < 1 or > MaxBursts || GapSteps is < 0 or > MaxGapSteps)
            throw new ArgumentException("Invalid LF card cycle mode.");
        return [(byte)Mode, (byte)Bursts, (byte)GapSteps];
    }

    /// <summary>This cycle with the mode from a 5911 reply or a 5912 entry.</summary>
    public LfCardCycle WithMode(ReadOnlySpan<byte> data)
    {
        if (data.Length != ModeSize || data[0] > (byte)LfCycleMode.WhilePresent || data[1] is < 1 or > MaxBursts || data[2] > MaxGapSteps)
            throw new ArgumentException("Invalid LF card cycle mode response.");
        return this with { Mode = (LfCycleMode)data[0], Bursts = data[1], GapSteps = data[2] };
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

    /// <summary>The same mode and cards in the same order (records compare the card arrays by reference).</summary>
    public bool SameAs(LfCardCycle other) => Mode == other.Mode && Bursts == other.Bursts && GapSteps == other.GapSteps &&
        Extras.Length == other.Extras.Length && Extras.Zip(other.Extras).All(p => p.First.SameAs(p.Second));
}
