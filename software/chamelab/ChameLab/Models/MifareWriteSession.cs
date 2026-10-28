using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Protocol;

namespace ChameLab.Models;

/// <summary>One physical target and one immutable write plan. Only acknowledged writes are retained.</summary>
public sealed class MifareWriteSession
{
    private readonly HashSet<int> _written = new();
    private readonly HashSet<int> _planned;
    public MifareDump Source { get; }
    public MifareDump? TargetKeys { get; private set; }
    public MagicGeneration Generation { get; }
    public bool WriteGen2Block0 { get; }
    public IReadOnlyCollection<int> WrittenBlocks => _written;
    public int Total => _planned.Count;
    public bool IsComplete => _planned.All(_written.Contains);
    internal bool Started { get; set; }

    public MifareWriteSession(MifareDump source, MagicGeneration generation,
        bool writeGen2Block0 = false, MifareDump? targetKeys = null)
    {
        Source = Copy(source);
        Generation = generation;
        WriteGen2Block0 = writeGen2Block0;
        TargetKeys = targetKeys is null ? null : Copy(targetKeys);
        _planned = Enumerable.Range(0, source.BlockCount)
            .Where(b => (b != 0 || generation != MagicGeneration.Gen2 || writeGen2Block0)
                && source.GetBlock(b) is { Length: 16 }).ToHashSet();
    }

    internal bool NeedsWrite(int block) => _planned.Contains(block) && !_written.Contains(block);

    internal void ValidatePlan(MifareDump source, MagicGeneration generation, bool writeGen2Block0)
    {
        if (Generation != generation || WriteGen2Block0 != writeGen2Block0 || Source.Type != source.Type
            || !Source.Uid.AsSpan().SequenceEqual(source.Uid)
            || Enumerable.Range(0, Source.BlockCount).Any(b =>
                !((Source.GetBlock(b) is null && source.GetBlock(b) is null)
                    || Source.GetBlock(b) is { } expected && source.GetBlock(b) is { } actual
                    && expected.AsSpan().SequenceEqual(actual))))
            throw new InvalidOperationException("The write plan changed. Start a new write session.");
    }

    internal void ValidateTarget(byte[] uid, byte[] atqa, byte sak)
    {
        if (TargetKeys is { } target && !target.Uid.AsSpan().SequenceEqual(uid))
            throw new InvalidOperationException("The target UID changed. Place the same card on the reader to continue writing.");
        TargetKeys ??= new MifareDump(MifareClassic.FromSak(sak), (byte[])uid.Clone(), atqa, sak);
        if (TargetKeys.BlockCount < Source.BlockCount)
            throw new InvalidOperationException("The target card is smaller than the source dump.");
    }

    internal void StoreKeys(int sector, byte[]? keyA, byte[]? keyB)
    {
        if (TargetKeys is not { } target) return;
        if (keyA is not null) target.KeyA[sector] = (byte[])keyA.Clone();
        if (keyB is not null) target.KeyB[sector] = (byte[])keyB.Clone();
    }

    internal void Confirm(int block)
    {
        _written.Add(block);
        var data = Source.GetBlock(block)!;
        if (block == 0 && TargetKeys is { } identity)
            TargetKeys = Copy(identity, data[..identity.Uid.Length]);
        if (MifareClassic.IsTrailer(block)) StoreKeys(MifareClassic.SectorOfBlock(block), data[..6], data[10..]);
    }

    private static MifareDump Copy(MifareDump source, byte[]? uid = null)
    {
        var copy = new MifareDump(source.Type, uid ?? (byte[])source.Uid.Clone(), (byte[])source.Atqa.Clone(), source.Sak);
        for (var b = 0; b < source.BlockCount; b++)
            if (source.GetBlock(b) is { } data) copy.SetBlock(b, (byte[])data.Clone());
        for (var s = 0; s < source.SectorCount; s++)
        {
            copy.KeyA[s] = source.KeyA[s] is { } a ? (byte[])a.Clone() : null;
            copy.KeyB[s] = source.KeyB[s] is { } b ? (byte[])b.Clone() : null;
        }
        return copy;
    }
}
