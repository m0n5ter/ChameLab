using ChameLab.Crypto;
using ChameLab.Services;
using System.Text.Json;
using Xunit;

namespace ChameLab.Cli.Tests;

public class CaptureTests
{
    // A firmware-format capture: UID, wire-order ATQA, SAK, ATS, one APDU/response.
    private static byte[] Packet => Convert.FromHexString("0401020304040020020278010500B2010C00060070025A009000");

    [Fact]
    public void EmvJsonRoundtripKeepsApdusAndWireIdentity()
    {
        var capture = EmvCapture.Decode(Packet);
        Assert.Equal("0004", capture.ToJson()["Card"]!["Contactless"]!["ATQA"]!.GetValue<string>());
        var restored = EmvCapture.FromJson(capture.ToJson());
        Assert.Equal(capture.Uid, restored.Uid); Assert.Equal(capture.Atqa, restored.Atqa);
        Assert.Equal(capture.Ats, restored.Ats);
        Assert.Equal(capture.Pairs[0].Command, restored.Pairs[0].Command);
        Assert.Equal(capture.Pairs[0].Response, restored.Pairs[0].Response);
    }

    [Fact]
    public void EmvRejectsEveryTruncationAndTrailingBytes()
    {
        var packet = Packet;
        for (var i = 0; i < packet.Length; i++) Assert.Throws<InvalidDataException>(() => EmvCapture.Decode(packet[..i]));
        Assert.Throws<InvalidDataException>(() => EmvCapture.Decode([.. packet, 0]));
    }

    [Theory]
    [InlineData("9F")]
    [InlineData("5A800000")]
    [InlineData("5A030001")]
    [InlineData("5A8201")]
    [InlineData("9F81818100")]
    public void TlvRejectsMalformedLengthsAndTags(string hex) => Assert.Throws<InvalidDataException>(() => BerTlv.Read(Convert.FromHexString(hex)));

    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(65535)]
    public void TlvLongLengthsRoundtrip(int size)
    {
        var value = Enumerable.Repeat((byte)0xA5, size).ToArray();
        var tlv = Assert.Single(BerTlv.Read(BerTlv.Encode("9F12", value)));
        Assert.Equal("9F12", tlv.Tag); Assert.Equal(value, tlv.Value);
    }

    [Fact]
    public void AesCbcMatchesNistSp80038aVector()
    {
        var key = Convert.FromHexString("2B7E151628AED2A6ABF7158809CF4F3C");
        var iv = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        var plain = Convert.FromHexString("6BC1BEE22E409F96E93D7E117393172A");
        var encrypted = Convert.FromHexString("7649ABAC8119B246CEE98E9B12E9197D");
        Assert.Equal(encrypted, CardCipher.Cbc(true, true, key, iv, plain));
        Assert.Equal(plain, CardCipher.Cbc(false, true, key, iv, encrypted));
    }

    [Fact]
    public void DefaultWeakDesKeyIsSupported()
    {
        Assert.Equal(Convert.FromHexString("8CA64DE9C1B123A7"), CardCipher.Cbc(true, false, new byte[8], new byte[8], new byte[8]));
    }

    [Fact]
    public void UlcgMatchesIndependentOpenSslFixturesForBothGeneratorsAndAllSegments()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ulcg.json")));
        foreach (var vector in fixtures.RootElement.GetProperty("vectors").EnumerateArray())
        {
            byte[] Hex(string name) => Convert.FromHexString(vector.GetProperty(name).GetString()!);
            var kind = Enum.Parse<UlcNonceGenerator>(vector.GetProperty("generator").GetString()!);
            Assert.Equal(kind, UlcgRecovery.Detect(Hex("zeroKeyNonce")));
            Assert.True(UlcgRecovery.Valid(Hex("plain"), kind));
            var index = vector.GetProperty("index").GetUInt32();
            var recovered = UlcgRecovery.RecoverSegment(Hex("encrypted"), Hex("baseKey"), vector.GetProperty("segment").GetInt32(), kind,
                threads: 2, startIndex: index - 3, endIndex: index + 4);
            Assert.Equal(Hex("key"), recovered);
        }
    }

    [Fact]
    public void UlcgHonorsCancellationBeforeSearch()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => UlcgRecovery.RecoverSegment(new byte[8], new byte[16], 0,
            UlcNonceGenerator.Giantec, 1, cts.Token));
    }

    [Fact]
    public void HardnestedBinaryKeepsLegacyHeaderNoncePairsAndParityOrder()
    {
        var capture = new HardnestedCapture(0x12345678, [new(0x01020304, 0xA), new(0x05060708, 0xB)]);
        Assert.Equal(Convert.FromHexString("123456782F010102030405060708AB"), HardnestedCaptureCodec.EncodeBinary(capture, 47, true));
        Assert.Throws<ArgumentException>(() => HardnestedCaptureCodec.EncodeBinary(new(1, [new(2, 0)]), 0, false));
    }

    [Fact]
    public void SectorKeysAcceptLegacyTextAndBinaryFchkExport()
    {
        var text = System.Text.Encoding.UTF8.GetBytes("FFFFFFFFFFFF:A0A1A2A3A4A5\n000000000000:D3F7D3F7D3F7\n");
        var parsed = CliSession.ReadSectorKeys(text, 2);
        var binary = parsed.SelectMany(p => p.A.Concat(p.B)).ToArray();
        var restored = CliSession.ReadSectorKeys(binary, 2);
        Assert.Equal(parsed[1].B, restored[1].B);
        Assert.Throws<ArgumentException>(() => CliSession.ReadSectorKeys(text, 3));
    }

    [Fact]
    public void EmvChecksFirmwareLimitsBeforeInstallingAnyResponses()
    {
        var capture = EmvCapture.Decode(Packet);
        CliSession.ValidateEmvInstallation(capture);
        Assert.Throws<ArgumentException>(() => CliSession.ValidateEmvInstallation(capture with { Pairs = [new([1], new byte[254])] }));
        Assert.Throws<ArgumentException>(() => CliSession.ValidateEmvInstallation(capture with { Pairs = [new(new byte[17], [0x90, 0]), new(new byte[18], [0x90, 0])] }));
    }
}
