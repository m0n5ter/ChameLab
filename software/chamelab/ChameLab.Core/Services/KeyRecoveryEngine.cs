using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Services;

public enum KeyRecoveryOperation { Nested, StaticNested, StaticEncryptedNested, FilterStaticEncrypted, Darkside, Mfkey32 }

/// <summary>Serializable CPU work; device access and UI state stay in the calling runtime.</summary>
public sealed record KeyRecoveryRequest(KeyRecoveryOperation Operation)
{
    public uint Uid { get; init; }
    public uint Distance { get; init; }
    public MfKeyType KeyType { get; init; }
    public NestedNonce[] Nonces { get; init; } = [];
    public DarksideItem[] Items { get; init; } = [];
    public DetectionLog[] Logs { get; init; } = [];
    public ulong[] Keys { get; init; } = [];
    public ulong[] OtherKeys { get; init; } = [];
}

public interface IKeyRecoveryEngine
{
    Task<IReadOnlyList<ulong>> RecoverAsync(KeyRecoveryRequest request, CancellationToken ct);
}

/// <summary>Native platforms use the thread pool; browsers register a Web Worker engine.</summary>
public sealed class ManagedKeyRecoveryEngine : IKeyRecoveryEngine
{
    public Task<IReadOnlyList<ulong>> RecoverAsync(KeyRecoveryRequest request, CancellationToken ct) =>
        Task.Run(() => Execute(request, ct), ct);

    public static IReadOnlyList<ulong> Execute(KeyRecoveryRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var n = request.Nonces;
        return request.Operation switch
        {
            KeyRecoveryOperation.Nested => NestedRecovery.Recover(request.Uid, request.Distance,
                n[0].Nt, n[0].NtEnc, n[0].Parity, n[1].Nt, n[1].NtEnc, n[1].Parity, ct),
            KeyRecoveryOperation.StaticNested => StaticNestedRecovery.Recover(request.Uid, request.KeyType,
                n[0].Nt, n[0].NtEnc, n[1].Nt, n[1].NtEnc, ct),
            KeyRecoveryOperation.StaticEncryptedNested => StaticEncryptedNestedRecovery.Recover(request.Uid, n[0], ct),
            KeyRecoveryOperation.FilterStaticEncrypted => StaticEncryptedNestedRecovery.Filter(
                n[0].Nt, request.Keys, n[1].Nt, request.OtherKeys, ct),
            KeyRecoveryOperation.Darkside => DarksideRecovery.Recover(request.Uid, request.Items, ct),
            KeyRecoveryOperation.Mfkey32 => Mfkey32Recovery.Recover(request.Logs[0], request.Logs[1]) is { } key
                ? new[] { key } : Array.Empty<ulong>(),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
    }
}

// Keep 48/64-bit numbers inside JSON strings during JS transport to avoid rounding.
[JsonSerializable(typeof(KeyRecoveryRequest))]
[JsonSerializable(typeof(ulong[]))]
public partial class KeyRecoveryJsonContext : JsonSerializerContext;
