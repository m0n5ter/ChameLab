using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using Xunit;

namespace ChameLab.Tests;

public class NdefTests
{
    /// <summary>A factory-fresh NTAG213: UID pages, CC E1 10 12 00, an empty NDEF TLV, zeros, config pages.</summary>
    private static List<byte[]> BlankNtag213()
    {
        var pages = Enumerable.Range(0, 45).Select(_ => new byte[4]).ToList();
        pages[0] = [0x04, 0x11, 0x22, 0xBF];
        pages[3] = [0xE1, 0x10, 0x12, 0x00];
        pages[4] = [0x03, 0x00, 0xFE, 0x00];
        pages[41] = [0x04, 0x00, 0x00, 0xFF]; // CFG0: AUTH0 = FF
        pages[43] = [0xFF, 0xFF, 0xFF, 0xFF];
        return pages;
    }

    private static List<byte[]> WithArea(List<byte[]> pages, byte[] area)
    {
        for (var i = 0; i < (area.Length + 3) / 4; i++)
            pages[Type2Ndef.FirstPage + i] = area[(i * 4)..Math.Min(area.Length, i * 4 + 4)].Concat(new byte[4]).Take(4).ToArray();
        return pages;
    }

    [Fact]
    public void Uri_UsesLongestPrefix_AndRoundTrips()
    {
        var r = Ndef.Uri("https://www.example.com/a");
        Assert.Equal(0x02, r.Payload[0]);
        Assert.True(Ndef.TryUri(r, out var uri));
        Assert.Equal("https://www.example.com/a", uri);
    }

    [Fact]
    public void Message_RoundTrips_WithIdAndLongPayload()
    {
        var records = new List<NdefRecord>
        {
            Ndef.Text("Привет", "ru"),
            new(NdefTnf.Media, "application/octet-stream"u8.ToArray(), [0x01, 0x02], Enumerable.Range(0, 300).Select(i => (byte)i).ToArray()),
            Ndef.AndroidApp("com.example.app"),
        };
        var parsed = Ndef.Parse(Ndef.Encode(records));
        Assert.Equal(3, parsed.Count);
        Assert.True(Ndef.TryText(parsed[0], out var text, out var lang));
        Assert.Equal(("Привет", "ru"), (text, lang));
        Assert.Equal(records[1].Payload, parsed[1].Payload);
        Assert.Equal(records[1].Id, parsed[1].Id);
        Assert.True(Ndef.TryAndroidApp(parsed[2], out var package));
        Assert.Equal("com.example.app", package);
    }

    [Fact]
    public void Wifi_RoundTrips()
    {
        var wifi = new NdefWifi("Home", NdefWifiAuth.Wpa2Personal, "secret123");
        Assert.True(Ndef.TryWifi(Ndef.Wifi(wifi), out var back));
        Assert.Equal(wifi, back);
    }

    [Fact]
    public void Parse_JoinsChunks()
    {
        // MB|CF|SR text "ab", then ME|SR unchanged "c".
        byte[] message = [0xB1, 0x01, 0x02, (byte)'T', 0x02, (byte)'e', 0x56, 0x00, 0x03, (byte)'n', (byte)'h', (byte)'i'];
        var r = Assert.Single(Ndef.Parse(message));
        Assert.True(Ndef.TryText(r, out var text, out var lang));
        Assert.Equal(("hi", "en"), (text, lang));
    }

    [Fact]
    public void Parse_RejectsTruncatedRecord()
    {
        Assert.Throws<FormatException>(() => Ndef.Parse([0xD1, 0x01, 0x10, (byte)'U', 0x01]));
    }

    [Fact]
    public void Type2_ReadsEmptyMessageOfBlankCard()
    {
        var area = Type2Ndef.Read(BlankNtag213(), TagSpecificType.Ntag213);
        Assert.True(area.IsFormatted);
        Assert.Equal(144, area.Capacity);
        Assert.NotNull(area.Records);
        Assert.Empty(area.Records!);
        Assert.Null(area.Error);
    }

    [Fact]
    public void Type2_BuildThenRead_KeepsLockControlTlv()
    {
        var pages = BlankNtag213();
        // Lock control TLV in front of the message, as some cards come formatted.
        WithArea(pages, [0x01, 0x03, 0xA0, 0x0C, 0x34, 0x03, 0x00, 0xFE]);
        var message = Ndef.Encode([Ndef.Uri("https://example.com")]);

        var (area, cc) = Type2Ndef.Build(pages, TagSpecificType.Ntag213, message);
        Assert.Null(cc);
        Assert.Equal(new byte[] { 0x01, 0x03, 0xA0, 0x0C, 0x34, 0x03, (byte)message.Length }, area[..7]);
        Assert.Equal(0xFE, area[7 + message.Length]);

        var read = Type2Ndef.Read(WithArea(pages, area), TagSpecificType.Ntag213);
        var r = Assert.Single(read.Records!);
        Assert.True(Ndef.TryUri(r, out var uri));
        Assert.Equal("https://example.com", uri);
    }

    [Fact]
    public void Type2_BuildUsesLongTlvLength()
    {
        var pages = Enumerable.Range(0, 135).Select(_ => new byte[4]).ToList();
        pages[3] = [0xE1, 0x10, 0x3E, 0x00];
        var message = Ndef.Encode([Ndef.Text(new string('x', 300), "en")]);
        var (area, _) = Type2Ndef.Build(pages, TagSpecificType.Ntag215, message);
        Assert.Equal(new byte[] { 0x03, 0xFF, (byte)(message.Length >> 8), (byte)message.Length }, area[..4]);
        var read = Type2Ndef.Read(WithArea(pages, area), TagSpecificType.Ntag215);
        Assert.Single(read.Records!);
    }

    [Fact]
    public void Type2_BuildRejectsTooLongMessage()
    {
        var message = Ndef.Encode([Ndef.Text(new string('x', 200), "en")]);
        Assert.Throws<FormatException>(() => Type2Ndef.Build(BlankNtag213(), TagSpecificType.Ntag213, message));
    }

    [Fact]
    public void Type2_BlankCcIsFormatted()
    {
        var pages = BlankNtag213();
        pages[3] = new byte[4];
        Assert.False(Type2Ndef.Read(pages, TagSpecificType.Ntag213).IsFormatted);
        var (_, cc) = Type2Ndef.Build(pages, TagSpecificType.Ntag213, []);
        Assert.Equal(new byte[] { 0xE1, 0x10, 0x12, 0x00 }, cc);
    }

    [Fact]
    public void Protection_ReadsAndSetsConfigPages()
    {
        var pages = BlankNtag213();
        var p = UltralightProtection.FromPages(pages, TagSpecificType.Ntag213)!;
        Assert.False(p.IsEnabled(TagSpecificType.Ntag213));

        var on = p with { Auth0 = 4, ReadProtected = true, AuthLimit = 3 };
        Assert.True(on.IsEnabled(TagSpecificType.Ntag213));
        // NFC_CNT_EN (bit 4) is kept, PROT and AUTHLIM set.
        Assert.Equal(0x93, on.Access(0x10));
        Assert.Equal(0x10, (on with { ReadProtected = false, AuthLimit = 0 }).Access(0x93));
    }

    [Fact]
    public void Protection_NeedsConfigPagesRead()
    {
        var pages = BlankNtag213();
        pages[43] = [];
        Assert.Null(UltralightProtection.FromPages(pages, TagSpecificType.Ntag213));
        Assert.Null(UltralightProtection.FromPages(BlankNtag213(), TagSpecificType.Mf0Icu1));
    }
}
