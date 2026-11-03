using System.Text;
using ChameLab.Models;
using Xunit;

namespace ChameLab.Tests;

public class NdefVCardTests
{
    [Fact]
    public void WritesAndReadsBackTheFields()
    {
        var card = new NdefVCard("Ann Lee", "+15550100", "ann@example.com", "Acme; Labs", "https://example.com");
        var bytes = card.ToBytes();
        Assert.Equal(
            "BEGIN:VCARD\r\nVERSION:3.0\r\nN:;Ann Lee;;;\r\nFN:Ann Lee\r\nORG:Acme\\; Labs\r\nTEL:+15550100\r\n"
            + "EMAIL:ann@example.com\r\nURL:https://example.com\r\nEND:VCARD\r\n",
            Encoding.UTF8.GetString(bytes));
        Assert.True(NdefVCard.TryParse("text/vcard", bytes, out var read));
        Assert.Equal(card, read);
    }

    [Fact]
    public void ReadsParametersFoldedLinesAndTheStructuredName()
    {
        var text = "BEGIN:VCARD\nVERSION:2.1\nN:Lee;Ann\nTEL;TYPE=CELL:+1555\n 0100\nEND:VCARD";
        Assert.True(NdefVCard.TryParse("text/x-vcard", Encoding.UTF8.GetBytes(text), out var read));
        Assert.Equal("Ann Lee", read.Name);
        Assert.Equal("+15550100", read.Phone);
    }

    [Fact]
    public void LeavesCardsWithMoreThanItShowsAsMime()
    {
        var address = "BEGIN:VCARD\nVERSION:3.0\nFN:Ann\nADR:;;Main St;;;;\nEND:VCARD";
        Assert.False(NdefVCard.TryParse("text/vcard", Encoding.UTF8.GetBytes(address), out _));
        var twoPhones = "BEGIN:VCARD\nVERSION:3.0\nFN:Ann\nTEL:1\nTEL:2\nEND:VCARD";
        Assert.False(NdefVCard.TryParse("text/vcard", Encoding.UTF8.GetBytes(twoPhones), out _));
        Assert.False(NdefVCard.TryParse("text/plain", Encoding.UTF8.GetBytes("BEGIN:VCARD\nEND:VCARD"), out _));
    }
}
