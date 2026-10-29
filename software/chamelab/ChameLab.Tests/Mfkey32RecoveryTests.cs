using System;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public class Mfkey32RecoveryTests
{
    // First pair generated with the original C Crypto1 implementation.
    private static DetectionLog First => new(3, MfKeyType.A, false,
        Convert.FromHexString("12345678"), Convert.FromHexString("87654321"),
        Convert.FromHexString("E6B99E33"), Convert.FromHexString("3748C47B"));

    [Theory]
    [InlineData("uid")]
    [InlineData("block")]
    [InlineData("keytype")]
    [InlineData("nested")]
    [InlineData("short-nt")]
    [InlineData("long-nr")]
    [InlineData("empty-ar")]
    [InlineData("short-uid")]
    public void UnrelatedOrMalformedRecords_AreRejected(string mismatch)
    {
        var a = First;
        var b = mismatch switch
        {
            "uid" => a with { Uid = Convert.FromHexString("23456789") },
            "block" => a with { Block = 7 },
            "keytype" => a with { KeyType = MfKeyType.B },
            "nested" => a with { IsNested = true },
            "short-nt" => a with { Nt = new byte[3] },
            "long-nr" => a with { Nr = new byte[5] },
            "empty-ar" => a with { Ar = Array.Empty<byte>() },
            "short-uid" => a with { Uid = new byte[3] },
            _ => throw new ArgumentException(mismatch),
        };
        if (mismatch == "short-uid") a = a with { Uid = b.Uid };

        Assert.Null(Mfkey32Recovery.Recover(a, b));
        Assert.Null(Mfkey32Recovery.Recover(b, a));
    }

    [Theory]
    [InlineData("04010212345678")]
    [InlineData("04010203040512345678")]
    public void LongUid_UsesLastCascadeLevel(string uidHex)
    {
        var uid = Convert.FromHexString(uidHex);
        var a = First with { Uid = uid };
        var b = a with { Nr = Convert.FromHexString("5C62D730"), Ar = Convert.FromHexString("55F2311D") };
        Assert.Equal(0UL, Mfkey32Recovery.Recover(a, b));
    }

    [Fact]
    public void RepeatedSameTranscript_IsNotIndependentEvidence()
    {
        Assert.Null(Mfkey32Recovery.Recover(First, First));
    }

    [Fact]
    public void AmbiguousRecoveryUnderLoad_ReturnsNullInsteadOfThrowing()
    {
        // The validation pipeline stops its workers on a second valid key; under load the producing thread
        // used to throw OperationCanceledException (Crapto1Sharp 2.0.0). Many ambiguous recoveries at once make
        // that race likely.
        var results = new ulong?[16];
        System.Threading.Tasks.Parallel.For(0, results.Length,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => results[i] = Mfkey32Recovery.Recover(First, First));
        Assert.All(results, Assert.Null);
    }
}
