using ChameLab.Models;
using Xunit;

namespace ChameLab.Tests;

public class NdefUriTests
{
    [Fact]
    public void PhoneDropsSpacingAndReadsBack()
    {
        var uri = NdefUris.Phone("+1 (555) 010-0");
        Assert.Equal("tel:+15550100", uri);
        Assert.True(NdefUris.TryPhone(uri, out var number));
        Assert.Equal("+15550100", number);
        Assert.False(NdefUris.TryPhone("https://example.com", out _));
    }

    [Fact]
    public void EmailEscapesSubjectAndBody()
    {
        var uri = NdefUris.Email(new NdefEmail("me@example.com", "Hi & bye", "Line one\nTwo"));
        Assert.Equal("mailto:me@example.com?subject=Hi%20%26%20bye&body=Line%20one%0ATwo", uri);
        Assert.True(NdefUris.TryEmail(uri, out var email));
        Assert.Equal(new NdefEmail("me@example.com", "Hi & bye", "Line one\nTwo"), email);
        Assert.True(NdefUris.TryEmail("MAILTO:me@example.com", out email));
        Assert.Equal("", email.Subject);
        // Several recipients or other fields don't fit the form: they stay a link.
        Assert.False(NdefUris.TryEmail("mailto:a@x.com,b@x.com", out _));
        Assert.False(NdefUris.TryEmail("mailto:a@x.com?cc=b@x.com", out _));
    }

    [Fact]
    public void SmsKeepsTheMessage()
    {
        var uri = NdefUris.Sms(new NdefSms("+1 555 0100", "On my way"));
        Assert.Equal("sms:+15550100?body=On%20my%20way", uri);
        Assert.True(NdefUris.TrySms(uri, out var sms));
        Assert.Equal(new NdefSms("+15550100", "On my way"), sms);
        Assert.True(NdefUris.TrySms("sms:+15550100", out sms));
        Assert.Equal("", sms.Body);
    }

    [Fact]
    public void GeoTakesOnlyThePlainForm()
    {
        var uri = NdefUris.Geo(new NdefGeo(48.8584, -2.2945));
        Assert.Equal("geo:48.8584,-2.2945", uri);
        Assert.True(NdefUris.TryGeo(uri, out var geo));
        Assert.Equal(new NdefGeo(48.8584, -2.2945), geo);
        Assert.False(NdefUris.TryGeo("geo:91,0", out _));
        Assert.False(NdefUris.TryGeo("geo:48.8,2.2,35", out _));
        Assert.False(NdefUris.TryGeo("geo:0,0?q=Eiffel+Tower", out _));
    }
}
