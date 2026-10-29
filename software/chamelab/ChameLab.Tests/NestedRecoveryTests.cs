using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Crypto;
using Xunit;

namespace ChameLab.Tests;

/// <summary>
/// Validates the managed nested / static-nested solvers by generating protocol-faithful
/// samples from a known key with the same crypto1 primitives the firmware uses, then checking
/// the solver lists that key as a candidate (the orchestrator auth-verifies candidates on the
/// card, so "key is among candidates" is the real requirement).
/// </summary>
public class NestedRecoveryTests
{
    private const ulong Key = 0xA0A1A2A3A4A5;
    private const uint Uid = 0x12345678;

    /// <summary>Keystream word produced while absorbing uid^ntp from the key state (the value the recovery inverts).</summary>
    private static (uint NtEnc, uint Ks1) Encrypt(uint ntp)
    {
        var state = new Crypto1State(Key);
        var ks1 = Crypto1.Crypto1Word(ref state, Uid ^ ntp, false);
        return (ntp ^ ks1, ks1);
    }

    private static byte Parity(uint ntp, uint ntEnc, uint ks1)
    {
        int P(uint v, int shift) => Crypto1.OddParity8((byte)((v >> shift) & 0xFF));
        var p0 = P(ntp, 24) ^ P(ntEnc, 24) ^ (int)((ks1 >> 16) & 1);
        var p1 = P(ntp, 16) ^ P(ntEnc, 16) ^ (int)((ks1 >> 8) & 1);
        var p2 = P(ntp, 8) ^ P(ntEnc, 8) ^ (int)(ks1 & 1);
        return (byte)(p0 | (p1 << 1) | (p2 << 2));
    }

    [Fact]
    public void Nested_RecoversKnownKey()
    {
        // Two nested samples with the target plaintext nonce sitting at the middle of the
        // sweep (dist = 14 → the sweep starts at the nonce itself).
        uint ntp0 = 0xABCDEF01, ntp1 = 0x55AA33CC;
        var (ntEnc0, ks0) = Encrypt(ntp0);
        var (ntEnc1, ks1) = Encrypt(ntp1);

        var keys = NestedRecovery.Recover(
            Uid, dist: 14,
            ntp0, ntEnc0, Parity(ntp0, ntEnc0, ks0),
            ntp1, ntEnc1, Parity(ntp1, ntEnc1, ks1));

        Assert.Contains(Key, keys);
    }

    [Fact]
    public void StaticNested_Gen1_RecoversKnownKey()
    {
        // Static gen1 nonce; the solver derives the plaintext nonce as prng_successor(nt, 160)
        // for the first sample and +160 for the second.
        const uint staticNt = 0x01200145;
        var ntp0 = Crypto1.PrngSuccessor(staticNt, 160);
        var ntp1 = Crypto1.PrngSuccessor(staticNt, 320);
        var (ntEnc0, _) = Encrypt(ntp0);
        var (ntEnc1, _) = Encrypt(ntp1);

        var keys = StaticNestedRecovery.Recover(
            Uid, MfKeyType.A, staticNt, ntEnc0, staticNt, ntEnc1);

        Assert.Contains(Key, keys);
    }
}
