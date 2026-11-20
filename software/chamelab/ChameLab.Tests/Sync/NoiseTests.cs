using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChameLab.Sync;
using Xunit;

namespace ChameLab.Tests.Sync;

public class NoiseTests
{
    /// <summary>The XX vectors for our suite from cacophony (github.com/haskell-cryptography/cacophony).</summary>
    [Fact]
    public void XxMatchesCacophonyVectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reference", "noise_vectors.json")));
        var vector = doc.RootElement.GetProperty("vectors").EnumerateArray()
            .Single(v => v.GetProperty("protocol_name").GetString() == "Noise_XX_25519_ChaChaPoly_SHA256");
        byte[] Hex(string name) => Convert.FromHexString(vector.GetProperty(name).GetString()!);

        var init = new NoiseHandshake(true, new KeyPair(Hex("init_static")), Hex("init_prologue"),
            ephemeral: new KeyPair(Hex("init_ephemeral")));
        var resp = new NoiseHandshake(false, new KeyPair(Hex("resp_static")), Hex("resp_prologue"),
            ephemeral: new KeyPair(Hex("resp_ephemeral")));

        var messages = vector.GetProperty("messages").EnumerateArray().ToList();
        CipherState? initSend = null, initReceive = null, respSend = null, respReceive = null;
        for (var i = 0; i < messages.Count; i++)
        {
            var payload = Convert.FromHexString(messages[i].GetProperty("payload").GetString()!);
            var expected = Convert.FromHexString(messages[i].GetProperty("ciphertext").GetString()!);
            var fromInitiator = i % 2 == 0;
            if (!init.IsComplete)
            {
                var (writer, reader) = fromInitiator ? (init, resp) : (resp, init);
                var ciphertext = writer.WriteMessage(payload);
                Assert.Equal(expected, ciphertext);
                Assert.Equal(payload, reader.ReadMessage(ciphertext));
                if (init.IsComplete)
                {
                    Assert.Equal(Hex("handshake_hash"), init.HandshakeHash);
                    Assert.Equal(init.HandshakeHash, resp.HandshakeHash);
                    (initSend, initReceive) = init.Split();
                    (respSend, respReceive) = resp.Split();
                }
            }
            else
            {
                var (send, receive) = fromInitiator ? (initSend!, respReceive!) : (respSend!, initReceive!);
                var ciphertext = send.Encrypt([], payload);
                Assert.Equal(expected, ciphertext);
                Assert.Equal(payload, receive.Decrypt([], ciphertext));
            }
        }
        Assert.Equal(new KeyPair(Hex("resp_static")).Public, init.RemoteStatic);
        Assert.Equal(new KeyPair(Hex("init_static")).Public, resp.RemoteStatic);
    }

    [Fact]
    public void TamperedMessageFails()
    {
        var init = new NoiseHandshake(true, KeyPair.Generate());
        var resp = new NoiseHandshake(false, KeyPair.Generate());
        resp.ReadMessage(init.WriteMessage([]));
        var second = resp.WriteMessage(Encoding.UTF8.GetBytes("hi"));
        second[^1] ^= 1;
        Assert.Throws<CryptographicException>(() => init.ReadMessage(second));
    }

    [Fact]
    public void Rfc5869HkdfVector()
    {
        // RFC 5869 test case 1.
        var ikm = Convert.FromHexString("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
        var salt = Convert.FromHexString("000102030405060708090a0b0c");
        var info = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9");
        Assert.Equal("3CB25F25FAACD57A90434F64D0362F2A2D2D0A90CF1A5A4C5DB02D56ECC4C5BF34007208D5B887185865",
            Convert.ToHexString(SyncCrypto.Hkdf(ikm, salt, info, 42)));
    }
}
