using System;
using System.Globalization;
using System.Text;

namespace ChameLab.Services;

/// <summary>The upstream solver's nonce file format, shared by every platform.</summary>
public static class HardnestedCaptureCodec
{
    public static string Encode(HardnestedCapture capture)
    {
        if (capture.Samples.Count == 0)
            throw new ArgumentException("No hardnested nonces were collected.", nameof(capture));
        var text = new StringBuilder();
        foreach (var sample in capture.Samples)
        {
            if (sample.EncryptedParity > 15)
                throw new ArgumentException("Invalid encrypted nonce parity.", nameof(capture));
            text.Append(sample.EncryptedNonce.ToString(CultureInfo.InvariantCulture));
            text.Append('|');
            text.Append(sample.EncryptedParity.ToString(CultureInfo.InvariantCulture));
            text.Append('\n');
        }
        return text.ToString();
    }
}
