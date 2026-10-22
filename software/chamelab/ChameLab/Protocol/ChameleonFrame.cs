using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace ChameLab.Protocol;

/// <summary>
/// A decoded device data frame: command id, status and payload.
/// </summary>
public readonly record struct ChameleonFrame(Command Command, Status Status, byte[] Data)
{
    public ushort RawCommand => (ushort)Command;
    public ushort RawStatus => (ushort)Status;
}

/// <summary>
/// Encodes and decodes the Chameleon Ultra wire format.
///
/// Frame layout (big-endian), identical to the reference client:
/// <code>
///   SOF(1=0x11) | LRC1(1) | CMD(2) | STATUS(2) | LEN(2) | LRC2(1) | DATA(LEN) | LRC3(1)
/// </code>
/// LRC bytes are 8-bit two's-complement checksums over everything preceding them.
/// </summary>
public static class FrameCodec
{
    public const byte Sof = 0x11;
    public const int MaxDataLength = 4096;

    // Byte offsets within a frame.
    private const int LenOffset = 6;     // start of the 2-byte length field
    private const int HeadLen = 8;       // SOF,LRC1,CMD,STATUS,LEN
    private const int Lrc2Offset = 8;
    private const int DataOffset = 9;    // first payload byte

    public static byte Lrc(ReadOnlySpan<byte> bytes)
    {
        var sum = 0;
        foreach (var b in bytes)
            sum = (sum + b) & 0xFF;
        return (byte)((0x100 - sum) & 0xFF);
    }

    public static byte[] Encode(Command cmd, ReadOnlySpan<byte> data = default, ushort status = 0)
    {
        var len = data.Length;
        var frame = new byte[DataOffset + len + 1];
        frame[0] = Sof;
        // frame[1] = LRC1 (set below)
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)cmd);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), status);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(LenOffset), (ushort)len);
        // frame[8] = LRC2 (set below)
        data.CopyTo(frame.AsSpan(DataOffset));

        frame[1] = Lrc(frame.AsSpan(0, 1));
        frame[Lrc2Offset] = Lrc(frame.AsSpan(0, HeadLen));
        frame[DataOffset + len] = Lrc(frame.AsSpan(0, DataOffset + len));
        return frame;
    }
}

/// <summary>
/// Incremental parser that turns a byte stream into <see cref="ChameleonFrame"/>s,
/// tolerating partial reads and resynchronizing on corruption.
/// </summary>
public sealed class FrameParser
{
    private readonly List<byte> _buffer = new(64);
    private int _pos;
    private ushort _cmd;
    private ushort _status;
    private ushort _length;

    /// <summary>Feed received bytes; yields every complete frame found.</summary>
    public IEnumerable<ChameleonFrame> Push(ReadOnlyMemory<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var frame = PushByte(bytes.Span[i]);
            if (frame is { } f)
                yield return f;
        }
    }

    private ChameleonFrame? PushByte(byte b)
    {
        _buffer.Add(b);

        if (_pos == 0)
        {
            if (b != FrameCodec.Sof) { Reset(); return null; }
        }
        else if (_pos == 1)
        {
            if (b != FrameCodec.Lrc(CollectionsMarshalSpan(0, 1))) { Reset(); return null; }
        }
        else if (_pos == 8)
        {
            if (b != FrameCodec.Lrc(CollectionsMarshalSpan(0, 8))) { Reset(); return null; }
            var span = CollectionsMarshalSpan(0, 8);
            _cmd = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2));
            _status = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(4));
            _length = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(6));
            if (_length > FrameCodec.MaxDataLength) { Reset(); return null; }
        }
        else if (_pos > 8)
        {
            var lrc3Index = 9 + _length;
            if (_pos == lrc3Index)
            {
                ChameleonFrame? result = null;
                if (b == FrameCodec.Lrc(CollectionsMarshalSpan(0, lrc3Index)))
                {
                    var data = new byte[_length];
                    _buffer.CopyTo(9, data, 0, _length);
                    result = new ChameleonFrame((Command)_cmd, (Status)_status, data);
                }
                Reset();
                return result;
            }
        }

        _pos++;
        return null;
    }

    private Span<byte> CollectionsMarshalSpan(int start, int count) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buffer).Slice(start, count);

    private void Reset()
    {
        _buffer.Clear();
        _pos = 0;
    }
}
