using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ChameLab.Services;

/// <summary>ChameLab's text nonce format, also used to send captures to the browser worker.</summary>
public static class HardnestedCaptureCodec
{
    /// <summary>Legacy nonces.bin: UID, target block/key type, then pairs of encrypted nonces and packed parity.</summary>
    public static byte[] EncodeBinary(HardnestedCapture capture, byte targetBlock, bool keyB)
    {
        if (capture.Samples.Count == 0 || capture.Samples.Count % 2 != 0)
            throw new ArgumentException("The binary format requires complete nonce pairs.", nameof(capture));
        var result = new byte[checked(6 + capture.Samples.Count / 2 * 9)];
        BinaryPrimitives.WriteUInt32BigEndian(result, capture.AuthenticationUid);
        result[4] = targetBlock; result[5] = keyB ? (byte)1 : (byte)0;
        for (var i = 0; i < capture.Samples.Count; i += 2)
        {
            var a = capture.Samples[i]; var b = capture.Samples[i + 1];
            if (a.EncryptedParity > 15 || b.EncryptedParity > 15)
                throw new ArgumentException("Invalid encrypted nonce parity.", nameof(capture));
            var offset = 6 + i / 2 * 9;
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset), a.EncryptedNonce);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset + 4), b.EncryptedNonce);
            result[offset + 8] = (byte)(a.EncryptedParity << 4 | b.EncryptedParity);
        }
        return result;
    }

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
