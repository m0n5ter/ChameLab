using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Services;

/// <summary>
/// Speaks the Gen4 Ultimate / GTU backdoor protocol (<c>0xCF &lt;4-byte pwd&gt; &lt;cmd&gt;</c>)
/// over raw ISO14443-A frames.
/// </summary>
public sealed class Gen4Client
{
    public const int RawTimeoutMs = 1000;

    private readonly Func<byte[], CancellationToken, Task<byte[]>> _exchange;
    private byte[] _password;

    public Gen4Client(ChameleonClient client, ReadOnlySpan<byte> password)
        : this((data, ct) => client.Send14aRawAsync(data, timeoutMs: RawTimeoutMs, checkResponseCrc: false, ct: ct), password)
    {
    }

    public Gen4Client(Func<byte[], CancellationToken, Task<byte[]>> exchange, ReadOnlySpan<byte> password)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        if (password.Length != 4) throw new ArgumentException("Password must be 4 bytes.", nameof(password));
        _exchange = exchange;
        _password = password.ToArray();
    }

    private Task<byte[]> CommandAsync(byte command, ReadOnlySpan<byte> data, CancellationToken ct)
    {
        var frame = new byte[6 + data.Length];
        frame[0] = 0xCF;
        _password.CopyTo(frame.AsSpan(1, 4));
        frame[5] = command;
        data.CopyTo(frame.AsSpan(6));
        return _exchange(frame, ct);
    }

    private static void CheckStatus(ReadOnlySpan<byte> response)
    {
        // Gen4 commands return ISO-style 90 00, optionally followed by two checksum / RF CRC bytes.
        if (response.Length is not (2 or 4) || response[0] != 0x90 || response[1] != 0x00)
            throw new InvalidOperationException(string.Format(Strings.Tools_Gen4CommandRejected, ToHex(response)));
    }

    public async Task<Gen4Configuration> ReadConfigurationAsync(CancellationToken ct = default) =>
        Gen4Configuration.FromResponse(await CommandAsync(0xC6, [], ct));

    public Task<byte[]> ReadVersionAsync(CancellationToken ct = default) =>
        CommandAsync(0xCC, [], ct);

    public async Task WriteConfigurationAsync(Gen4Configuration config, bool permanent = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        CheckStatus(await CommandAsync((byte)(permanent ? 0xF1 : 0xF0), config.RawBytes, ct));
        // FE is rejected on newer cards. F0/F1 changes the password atomically.
        _password = config.Password;
    }

    public async Task<byte[]> ReadBlockAsync(int block, CancellationToken ct = default)
    {
        if (block is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(block));
        var data = await CommandAsync(0xCE, [(byte)block], ct);
        if (data.Length is not (16 or 18))
            throw new FormatException(Strings.Tools_Gen4ExpectedBlock16);
        return data[..16];
    }

    public async Task WriteBlockAsync(int block, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (block is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(block));
        if (data.Length != 16) throw new ArgumentException("Block must be 16 bytes.", nameof(data));
        var payload = new byte[17];
        payload[0] = (byte)block;
        data.CopyTo(payload.AsSpan(1));
        CheckStatus(await CommandAsync(0xCD, payload, ct));
    }

    /// <summary>Reads memory-backed settings in either 16-byte blocks or 4-byte UL pages.</summary>
    public async Task<byte[]> ReadMemoryAsync(int firstBlock, int length, int stride, CancellationToken ct = default)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (stride is <= 0 or > 16) throw new ArgumentOutOfRangeException(nameof(stride));
        var result = new List<byte>(length);
        for (var offset = 0; offset < length; offset += stride)
        {
            var block = await ReadBlockAsync(firstBlock + offset / stride, ct);
            var take = Math.Min(stride, length - offset);
            result.AddRange(block.AsSpan(0, take));
        }
        return result.ToArray();
    }

    /// <summary>Writes memory-backed settings while preserving bytes outside the target range.</summary>
    public async Task WriteMemoryAsync(int firstBlock, byte[] data, int stride, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (stride is <= 0 or > 16) throw new ArgumentOutOfRangeException(nameof(stride));
        for (var offset = 0; offset < data.Length; offset += stride)
        {
            var address = firstBlock + offset / stride;
            var block = await ReadBlockAsync(address, ct);
            var count = Math.Min(stride, data.Length - offset);
            data.AsSpan(offset, count).CopyTo(block.AsSpan(0, count));
            await WriteBlockAsync(address, block, ct);
        }
    }

    /// <summary>Preserves manufacturer bytes and lock/OTP data when changing the UID.</summary>
    public async Task WriteUidAsync(Gen4Configuration config, byte[] uid, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(uid);
        if (config.UidLength == 0 || config.Protocol > 1 || uid.Length != config.UidLength)
            throw new ArgumentException("UID must match the configured UID length.", nameof(uid));

        if (config.Protocol == 1 && uid.Length == 7)
        {
            var b0 = await ReadBlockAsync(0, ct);
            var b1 = await ReadBlockAsync(1, ct);
            var b2 = await ReadBlockAsync(2, ct);
            uid.AsSpan(0, 3).CopyTo(b0);
            b0[3] = (byte)(0x88 ^ uid[0] ^ uid[1] ^ uid[2]);
            uid.AsSpan(3).CopyTo(b1);
            b2[0] = (byte)(uid[3] ^ uid[4] ^ uid[5] ^ uid[6]);
            await WriteBlockAsync(0, b0, ct);
            await WriteBlockAsync(1, b1, ct);
            await WriteBlockAsync(2, b2, ct);
        }
        else
        {
            var block = await ReadBlockAsync(0, ct);
            uid.CopyTo(block, 0);
            // Only four-byte Classic UIDs store a BCC after the UID;
            // seven-byte Classic layouts store SAK there.
            if (config.Protocol == 0 && uid.Length == 4)
            {
                byte bcc = 0;
                foreach (var b in uid) bcc ^= b;
                block[uid.Length] = bcc;
            }
            await WriteBlockAsync(0, block, ct);
        }
    }

    public static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    public static string ToSpacedHex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;
        var sb = new StringBuilder(bytes.Length * 3 - 1);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append($"{bytes[i]:X2}");
        }
        return sb.ToString();
    }

    public static byte[] ParseHex(string? text, int? length = null, int? maxLength = null)
    {
        var source = text ?? string.Empty;
        var sb = new StringBuilder(source.Length);
        foreach (var ch in source)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (!Uri.IsHexDigit(ch)) throw new FormatException(Strings.Common_NotHex);
            sb.Append(ch);
        }
        var clean = sb.ToString();
        if (clean.Length % 2 != 0
            || (length is { } exact && clean.Length != exact * 2)
            || (maxLength is { } max && clean.Length > max * 2))
        {
            var expected = length?.ToString() ?? $"0–{maxLength}";
            throw new FormatException(string.Format(Strings.Tools_Gen4HexRequired, expected));
        }
        return Convert.FromHexString(clean);
    }

    /// <summary>
    /// <c>0xCC</c> returns five version bytes, optionally followed by two RF CRC bytes.
    /// Unknown or truncated versions return <c>null</c> and require an explicit PACK page choice.
    /// </summary>
    public static int? PackPage(ReadOnlySpan<byte> version)
    {
        if (version.Length is not (5 or 7) || version[0] != 0 || version[1] != 0 || version[2] != 0)
            return null;
        var revision = (version[3] << 8) | version[4];
        return revision is 0x06A0 or 0x6666 ? 0x13 : 0xE6;
    }
}
