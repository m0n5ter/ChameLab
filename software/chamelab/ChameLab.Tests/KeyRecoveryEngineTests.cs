using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public class KeyRecoveryEngineTests
{
    // Exercise the exact request/result format used across the worker boundary.
    public static IReadOnlyList<ulong> RoundTrip(KeyRecoveryRequest request)
    {
        var json = JsonSerializer.Serialize(request, KeyRecoveryJsonContext.Default.KeyRecoveryRequest);
        var decoded = JsonSerializer.Deserialize(json, KeyRecoveryJsonContext.Default.KeyRecoveryRequest)!;
        var result = ManagedKeyRecoveryEngine.Execute(decoded).ToArray();
        return JsonSerializer.Deserialize(JsonSerializer.Serialize(result, KeyRecoveryJsonContext.Default.UInt64Array),
            KeyRecoveryJsonContext.Default.UInt64Array)!;
    }

    [Fact]
    public void FilterRoundTrip_PreservesFullIntegerPrecision()
    {
        ulong[] keys = [0, 0xFFFFFFFFFFFF, ulong.MaxValue];
        var result = RoundTrip(new(KeyRecoveryOperation.FilterStaticEncrypted)
            { Nonces = [new(123, 0, 0), new(123, 0, 0)], Keys = keys });
        Assert.Equal(keys, result);
    }

    [Fact]
    public void StaticEncryptedRoundTrip_MatchesReferenceC()
    {
        var keys = RoundTrip(new(KeyRecoveryOperation.StaticEncryptedNested)
            { Uid = 0xA13E4902, Nonces = [new(0xD14191B3, 0x2E9E49FC, 15)] });
        Assert.Equal(44075, keys.Count);
        ulong hash = 14695981039346656037UL;
        foreach (var key in keys.Order())
            for (var j = 0; j < 6; j++)
                hash = unchecked((hash ^ ((key >> (8 * j)) & 255)) * 1099511628211UL);
        Assert.Equal(0x79007A0A206AF947UL, hash);
    }

    [Fact]
    public async Task ManagedEngine_CancellationPreventsWork()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ManagedKeyRecoveryEngine()
            .RecoverAsync(new(KeyRecoveryOperation.Nested), new CancellationToken(true)));
    }
}
