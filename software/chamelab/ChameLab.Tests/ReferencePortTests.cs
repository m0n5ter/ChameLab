using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Crypto;
using Xunit;

namespace ChameLab.Tests;

public class ReferencePortTests
{
    public static IEnumerable<object[]> Vectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Reference", "crypto_vectors.json")));
        foreach (var vector in document.RootElement.EnumerateArray())
            yield return new object[] { vector.GetProperty("kind").GetString()!, vector.Clone() };
    }

    private static uint U(JsonElement v, string name) => v.GetProperty(name).GetUInt32();
    private static ulong H(JsonElement v, string name) => ulong.Parse(v.GetProperty(name).GetString()!, NumberStyles.HexNumber);
    private static byte[] Bytes(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void ManagedPort_MatchesOriginalC(string kind, JsonElement v)
    {
        var key = H(v, "key");
        switch (kind)
        {
            case "primitive":
                var state = new Crypto1State(key);
                var input = U(v, "input");
                var encrypted = U(v, "encrypted") != 0;
                Assert.Equal(U(v, "bit"), state.Crypto1Bit((byte)(input & 1), encrypted));
                Assert.Equal(U(v, "byte"), state.Crypto1Byte((byte)(input >> 4), encrypted));
                Assert.Equal(U(v, "word"), state.Crypto1Word(input, encrypted));
                Assert.Equal(H(v, "after"), state.Lfsr);
                state.LfsrRollbackWord(input, encrypted);
                state.LfsrRollbackByte((byte)(input >> 4), encrypted);
                state.LfsrRollbackBit((byte)(input & 1), encrypted);
                Assert.Equal(H(v, "back"), state.Lfsr);
                Assert.Equal(U(v, "prng"), Crypto1.PrngSuccessor(input, 42));
                break;
            case "mfkey":
                DetectionLog Log(int index) => new(3, MfKeyType.A, false, Bytes(U(v, "uid")),
                    Bytes(U(v, $"nt{index}")), Bytes(U(v, $"nr{index}")), Bytes(U(v, $"ar{index}")));
                Assert.Equal(key, Assert.Single(KeyRecoveryEngineTests.RoundTrip(new(KeyRecoveryOperation.Mfkey32)
                    { Logs = [Log(0), Log(1)] })));
                break;
            case "nested":
                var nestedKeys = KeyRecoveryEngineTests.RoundTrip(new(KeyRecoveryOperation.Nested)
                {
                    Uid = U(v, "uid"), Distance = U(v, "distance"),
                    Nonces = [new(U(v, "nt0"), U(v, "enc0"), (byte)U(v, "par0")),
                        new(U(v, "nt1"), U(v, "enc1"), (byte)U(v, "par1"))],
                });
                Assert.Contains(key, nestedKeys);
                Assert.Equal(v.GetProperty("cKeys").EnumerateArray()
                    .Select(k => ulong.Parse(k.GetString()!, NumberStyles.HexNumber)).Order(), nestedKeys.Order());
                break;
            case "static":
                Assert.Contains(key, KeyRecoveryEngineTests.RoundTrip(new(KeyRecoveryOperation.StaticNested)
                {
                    Uid = U(v, "uid"), KeyType = (MfKeyType)U(v, "knownType"),
                    Nonces = [new(U(v, "nonce"), U(v, "enc0"), 0), new(U(v, "nonce"), U(v, "enc1"), 0)],
                }));
                break;
            case "darkside":
                var keys = KeyRecoveryEngineTests.RoundTrip(new(KeyRecoveryOperation.Darkside)
                {
                    Uid = U(v, "uid"),
                    Items = [new DarksideItem(U(v, "nt"), H(v, "ks"), H(v, "par"), U(v, "nr"), U(v, "ar"))],
                });
                Assert.Equal((int)U(v, "cCount"), keys.Count);
                Assert.Equal(v.GetProperty("cContains").GetBoolean(), keys.Contains(key));
                break;
            case "darkside-zero":
                DarksideItem Item(int index) => new(U(v, "nt"), H(v, $"ks{index}"), 0, U(v, $"nr{index}"), U(v, "ar"));
                Assert.Empty(KeyRecoveryEngineTests.RoundTrip(new(KeyRecoveryOperation.Darkside)
                    { Uid = U(v, "uid"), Items = [Item(0)] }));
                var intersection = KeyRecoveryEngineTests.RoundTrip(new(KeyRecoveryOperation.Darkside)
                    { Uid = U(v, "uid"), Items = [Item(0), Item(1)] });
                Assert.Equal(v.GetProperty("cKeys").EnumerateArray()
                    .Select(k => ulong.Parse(k.GetString()!, NumberStyles.HexNumber)).Order(), intersection.Order());
                Assert.Contains(key, intersection);
                break;
            default:
                throw new InvalidOperationException($"Unknown C reference vector: {kind}");
        }
    }
}
