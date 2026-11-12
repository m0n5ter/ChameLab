using System;
using System.Collections.Generic;
using System.Collections;
using System.Runtime.CompilerServices;
using ChameLab.Protocol;

namespace ChameLab.Models;

/// <summary>Evidence and costs for one scanned card on one connection. A new dump/connection starts a new session.</summary>
public sealed class MifareRecoverySession
{
    private static readonly ConditionalWeakTable<MifareDump, MifareRecoverySession> Sessions = new();
    private object? _connection;
    public static MifareRecoverySession For(MifareDump dump, object connection)
    {
        var session = Sessions.GetOrCreateValue(dump);
        if (session._connection is not null && !ReferenceEquals(session._connection, connection))
        {
            Sessions.Remove(dump);
            session = Sessions.GetOrCreateValue(dump);
        }
        session._connection = connection;
        return session;
    }

    public MifareKeyRejections Rejected { get; } = new();
    public HashSet<(int Block, MfKeyType Type)> Refused { get; } = [];
    public Dictionary<(int Block, MfKeyType Type, ulong Key), NtDistance> Distances { get; } = [];
    public Dictionary<(int Block, MfKeyType Type), DarksideResult> Darkside { get; } = [];
    public MifareClassicPrngType? Prng { get; set; }
    public bool NestedUnreliable { get; set; }
    public bool BackdoorsChecked { get; set; }
    public HashSet<ulong> RejectedBackdoors { get; } = [];
    public HashSet<(int Block, MfKeyType Type, ulong Key)> NormalSenSeedsChecked { get; } = [];
    internal MifareSenCapture? SenCapture { get; set; }
    internal Dictionary<(int Sector, MfKeyType Type), IReadOnlyList<ulong>> SenCandidates { get; } = [];
    internal HashSet<(int Sector, MfKeyType Type)> SenExhausted { get; } = [];
    internal HashSet<int> SenPrepared { get; } = [];
    internal Dictionary<MfKeyType, ulong?> SenExtraKeys { get; } = [];
    internal HashSet<(int Sector, MfKeyType Type, string Dictionary)> SenDictionaries { get; } = [];
    internal HashSet<int> TrailersChecked { get; } = [];
    private double _authenticationMs, _candidateMs;
    private int _authSamples, _candidateSamples;
    public void RecordAuthentication(TimeSpan elapsed, int attempts = 1)
    {
        if (attempts <= 0 || elapsed <= TimeSpan.Zero) return;
        _authenticationMs += elapsed.TotalMilliseconds;
        _authSamples += attempts;
    }
    public void RecordCandidates(TimeSpan elapsed, int attempts)
    {
        if (attempts <= 0 || elapsed <= TimeSpan.Zero) return;
        _candidateMs += elapsed.TotalMilliseconds;
        _candidateSamples += attempts;
    }
    public double EstimateCandidateMilliseconds(int candidates) => candidates *
        (_candidateSamples > 0 ? _candidateMs / _candidateSamples : _authSamples > 0 ? _authenticationMs / _authSamples : 1);
    public static ulong Value(byte[] key)
    {
        ulong value = 0;
        foreach (var b in key) value = (value << 8) | b;
        return value;
    }
}

internal sealed record MifareSenCapture(uint Uid, System.Collections.Generic.IReadOnlyList<(NestedNonce A, NestedNonce B)> Sectors)
{
    internal (NestedNonce A, NestedNonce B)? Extra { get; init; }
}

/// <summary>One 80-bit sector mask per key, rather than up to 80 hash entries for that key.</summary>
public sealed class MifareKeyRejections : IReadOnlyCollection<(int Block, MfKeyType Type, ulong Key)>
{
    private readonly Dictionary<ulong, UInt128> _keys = [];
    public int Count { get; private set; }
    internal int DistinctKeyCount => _keys.Count;
    private static UInt128 Mask(int block, MfKeyType type) => (UInt128)1 <<
        (MifareClassic.SectorOfBlock(block) * 2 + (type == MfKeyType.A ? 0 : 1));
    public bool Contains((int Block, MfKeyType Type, ulong Key) item) =>
        _keys.TryGetValue(item.Key, out var rejected) && (rejected & Mask(item.Block, item.Type)) != 0;
    public bool Add((int Block, MfKeyType Type, ulong Key) item)
    {
        var mask = Mask(item.Block, item.Type);
        var rejected = _keys.GetValueOrDefault(item.Key);
        if ((rejected & mask) != 0) return false;
        _keys[item.Key] = rejected | mask;
        Count++;
        return true;
    }
    public IEnumerator<(int Block, MfKeyType Type, ulong Key)> GetEnumerator()
    {
        foreach (var (key, rejected) in _keys)
            for (var bit = 0; bit < 80; bit++)
                if ((rejected & ((UInt128)1 << bit)) != 0)
                    yield return (MifareClassic.SectorTrailerBlock(bit / 2), bit % 2 == 0 ? MfKeyType.A : MfKeyType.B, key);
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
