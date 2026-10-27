using System;
using System.Threading;
using ChameLab.Protocol;
using ChameLab.Services;
using Crapto1Sharp;
using Xunit;

namespace ChameLab.Tests;

public class StaticNestedRecoveryTests
{
    // Captured on a physical static-PRNG 1K test card. Both A and B use 160/320;
    // the old B-only 161/321 assumption produced 128,880 or 131,254 wrong keys.
    [Theory]
    [InlineData(MfKeyType.A, 0x2B7C6E42u, 0x1DE65248u, 0x112233445566UL)]
    [InlineData(MfKeyType.B, 0x2B7C6E42u, 0x1DE65248u, 0x112233445566UL)]
    [InlineData(MfKeyType.A, 0xDA3BD82Eu, 0xFCA5BBCFu, 0x112233445588UL)]
    [InlineData(MfKeyType.B, 0xDA3BD82Eu, 0xFCA5BBCFu, 0x112233445588UL)]
    public void PhysicalCardCapture_RecoversExactlyOneCorrectKey(
        MfKeyType type, uint enc0, uint enc1, ulong expected)
    {
        var keys = StaticNestedRecovery.Recover(0x4D4F4E53, type,
            0x009080A2, enc0, 0x009080A2, enc1);

        Assert.Equal(expected, Assert.Single(keys));
    }

    [Theory]
    [InlineData(0x01200145u, MfKeyType.A, 160)]
    [InlineData(0x01200145u, MfKeyType.B, 160)]
    [InlineData(0x009080A2u, MfKeyType.A, 160)]
    [InlineData(0x009080A2u, MfKeyType.B, 160)]
    [InlineData(0x009080A2u, MfKeyType.B, 161)]
    public void SupportedNonceOffsets_RecoversKnownKey(uint nonce, MfKeyType type, int distance)
    {
        const uint uid = 0x12345678;
        const ulong key = 0xA0A1A2A3A4A5;
        uint Encrypt(int offset)
        {
            var ntp = Crypto1.PrngSuccessor(nonce, offset);
            var state = new Crypto1State(key);
            return ntp ^ Crypto1.Crypto1Word(ref state, uid ^ ntp, false);
        }

        var keys = StaticNestedRecovery.Recover(uid, type,
            nonce, Encrypt(distance), nonce, Encrypt(distance + 160));

        Assert.Contains(key, keys);
        Assert.InRange(keys.Count, 1, 2);
    }

    [Fact]
    public void ContradictoryCaptures_DoNotReturnSingleSampleCandidateUnion()
    {
        var keys = StaticNestedRecovery.Recover(0x4D4F4E53, MfKeyType.B,
            0x009080A2, 0x2B7C6E42, 0x009080A2, 0xFCA5BBCF);

        Assert.Empty(keys);
    }

    [Fact]
    public void UnknownNonceGeneration_ReturnsEmpty()
    {
        Assert.Empty(StaticNestedRecovery.Recover(0x12345678, MfKeyType.A,
            0xDEADBEEF, 0, 0xDEADBEEF, 0));
    }

    [Fact]
    public void CanceledCalculation_StopsBeforeRecoveringStates()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        Assert.Throws<OperationCanceledException>(() => StaticNestedRecovery.Recover(
            0x4D4F4E53, MfKeyType.B, 0x009080A2, 0x2B7C6E42, 0x009080A2, 0x1DE65248, stop.Token));
    }
}
