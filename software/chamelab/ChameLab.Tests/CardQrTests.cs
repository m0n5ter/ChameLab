using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using Xunit;

namespace ChameLab.Tests;

public sealed class CardQrTests
{
    private static CardSave Classic(int sectors, int seed = 1)
    {
        var random = new Random(seed);
        var card = new CardSave
        {
            Name = "Office",
            Tag = sectors > 16 ? TagSpecificType.Mifare4096 : TagSpecificType.Mifare1024,
            Uid = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            Sak = 0x08,
            Atqa = new byte[] { 0x04, 0x00 },
            Color = "#2196F3",
            Folder = "Work",
        };
        var blocks = sectors > 32 ? 32 * 4 + (sectors - 32) * 16 : sectors * 4;
        for (var i = 0; i < blocks; i++)
        {
            var block = new byte[16];
            // Mostly empty like a real dump, with some random blocks (keys, data) that don't compress.
            if (i % 5 == 0) random.NextBytes(block);
            card.Data.Add(block);
        }
        card.Extra.UnknownKeyB.Add(3);
        return card;
    }

    private static CardSave Assemble(IEnumerable<string> links)
    {
        var collector = new CardQrCollector();
        var results = links.Select(collector.Add).ToList();
        Assert.Equal(CardQrScan.Complete, results[^1]);
        return collector.Card!;
    }

    [Fact]
    public void Lf_FitsOneCode_AndRoundTrips()
    {
        var card = new CardSave { Name = "Gate", Tag = TagSpecificType.Em410x, Uid = new byte[] { 1, 2, 3, 4, 5 } };

        var links = CardQr.Encode(card);

        Assert.Single(links);
        Assert.StartsWith(CardQr.WebAppUrl + "#card=1.1.1.", links[0]);
        var back = Assemble(links);
        Assert.Equal(card.Name, back.Name);
        Assert.Equal(card.Tag, back.Tag);
        Assert.Equal(card.Uid, back.Uid);
    }

    [Fact]
    public void Classic1K_RoundTripsEveryField_ButIdAndFolder()
    {
        var card = Classic(16);

        var back = Assemble(CardQr.Encode(card));

        var expected = card.ToJson();
        expected.Remove("id");
        expected.Remove("folder");
        var actual = back.ToJson();
        Assert.NotEqual(card.Id, back.Id);
        actual.Remove("id");
        Assert.Equal(expected.ToJsonString(), actual.ToJsonString());
    }

    [Fact]
    public void Classic4K_IsSplit_AndPartsArriveInAnyOrder()
    {
        var card = Classic(40);

        var links = CardQr.Encode(card);

        Assert.True(links.Count > 1);
        Assert.All(links, l => Assert.True(l.Length <= CardQr.WebAppUrl.Length + 40 + CardQr.MaxPartChars));
        var shuffled = links.Reverse().ToList();
        var collector = new CardQrCollector();
        Assert.Equal(CardQrScan.Added, collector.Add(shuffled[0]));
        Assert.Equal(CardQrScan.Repeated, collector.Add(shuffled[0]));
        Assert.Equal(links.Count - 1, collector.Missing.Count());
        foreach (var link in shuffled.Skip(1).Take(links.Count - 2)) Assert.Equal(CardQrScan.Added, collector.Add(link));
        Assert.Equal(CardQrScan.Complete, collector.Add(shuffled[^1]));
        Assert.Equal(card.Data.Count, collector.Card!.Data.Count);
        Assert.Equal(card.Data[5], collector.Card.Data[5]);
    }

    [Fact]
    public void Collector_StartsOver_WithAnotherCard()
    {
        var first = CardQr.Encode(Classic(40, seed: 1));
        var second = CardQr.Encode(Classic(40, seed: 2));
        var collector = new CardQrCollector();

        collector.Add(first[0]);
        collector.Add(second[1]);

        Assert.Equal(1, collector.Received);
        Assert.Contains(1, collector.Missing);
    }

    [Fact]
    public void Collector_RejectsTamperedParts()
    {
        var links = CardQr.Encode(Classic(40)).ToList();
        var last = links[^1];
        // Same id and count, but a different slice.
        links[^1] = last[..^4] + (last[^4] == 'A' ? "BBBB" : "AAAA");
        var collector = new CardQrCollector();
        foreach (var link in links.Take(links.Count - 1)) collector.Add(link);

        Assert.Throws<FormatException>(() => collector.Add(links[^1]));
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("hello")]
    [InlineData("https://m0n5ter.github.io/ChameLab/app/#card=2.1.1.0011aabb.AAAA")]
    [InlineData("https://m0n5ter.github.io/ChameLab/app/#card=1.2.1.0011aabb.AAAA")]
    [InlineData("https://m0n5ter.github.io/ChameLab/app/#card=1.1.1.0011aabb.AA+A")]
    public void TryParse_RejectsOtherCodes(string text)
    {
        Assert.False(CardQr.TryParse(text, out _));
    }

    [Fact]
    public void TryParse_AcceptsAnyHostAndBareFragment()
    {
        var link = CardQr.Encode(new CardSave { Name = "X", Tag = TagSpecificType.Em410x, Uid = new byte[5] })[0];
        var fragment = link[(link.IndexOf('#') + 1)..];

        Assert.True(CardQr.TryParse("http://localhost:5000/#" + fragment, out var a));
        Assert.True(CardQr.TryParse(fragment, out var b));
        Assert.True(CardQr.TryParse("#x=1&" + fragment, out var c));
        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void QrImage_RoundTripsThroughRenderedModules()
    {
        var link = CardQr.Encode(Classic(40))[0];
        var modules = QrCodes.Encode(link);

        // Drawn 3 pixels per module with a 4-module quiet zone, as a screenshot would hold it.
        const int scale = 3, quiet = 4;
        var size = (modules.GetLength(0) + 2 * quiet) * scale;
        var gray = Enumerable.Repeat((byte)255, size * size).ToArray();
        for (var y = 0; y < modules.GetLength(0); y++)
            for (var x = 0; x < modules.GetLength(1); x++)
                if (modules[y, x])
                    for (var dy = 0; dy < scale; dy++)
                        for (var dx = 0; dx < scale; dx++)
                            gray[((y + quiet) * scale + dy) * size + (x + quiet) * scale + dx] = 0;

        Assert.Equal(link, QrCodes.Decode(gray, size, size));
        // Readable off a screen: version 20 (97 modules) at most.
        Assert.True(modules.GetLength(0) <= 97, $"{modules.GetLength(0)} modules");
    }

    [Fact]
    public void QrImage_FindsNothing_InABlankImage()
    {
        Assert.Null(QrCodes.Decode(new byte[100 * 100], 100, 100));
    }
}
