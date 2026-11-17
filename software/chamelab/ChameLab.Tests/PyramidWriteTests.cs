using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Transport;
using Xunit;

namespace ChameLab.Tests;

public class PyramidWriteTests
{
    // The supplied card is a raw frame; preserve its bytes rather than rebuilding
    // it from decoded facility/card fields (which may discard format-specific bits).
    private static readonly byte[] Frame = Convert.FromHexString("0001014A3E0101010101016BE38CD64A");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WritesExactFrameAndRekeysBlankOrProtectedTag(bool protectedTag)
    {
        var tag = new TagTransport(protectedTag);
        await using var connection = new ChameleonConnection(tag);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var key = Convert.FromHexString("87654321");
        await client.WriteLfToT55xxAsync(TagSpecificType.FarpointePyramid, Frame, key,
            [Convert.FromHexString("FFFFFFFF"), Convert.FromHexString("12345678")]);
        Assert.Equal(Frame, tag.ReadFrame());
        Assert.Equal(0x00107090u, tag.Memory[0]);
        Assert.Equal(0x87654321u, tag.Memory[7]);
        Assert.Equal(0, tag.AcceptedBlocks[^1]);
        Assert.DoesNotContain(0, tag.AcceptedBlocks.TakeWhile(b => b != 4));
        Assert.Equal(Command.PyramidScan, tag.Requests[^1].Command);

        // A second write must work using the newly stored password as well.
        var second = PyramidFrame.Encode26(12, 3456);
        await client.WriteLfToT55xxAsync(TagSpecificType.FarpointePyramid, second,
            oldKeys: [key]);
        Assert.Equal(second, tag.ReadFrame());
        Assert.Equal(0x20206666u, tag.Memory[7]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsMissingOrDifferentReadback(bool missing)
    {
        var tag = new TagTransport(false) { MissingRead = missing, CorruptRead = !missing };
        await using var connection = new ChameleonConnection(tag);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<ChameleonException>(() => new ChameleonClient(connection)
            .WriteLfToT55xxAsync(TagSpecificType.FarpointePyramid, Frame));
    }

    [Fact]
    public async Task WrongPasswordCannotProduceSuccessfulWrite()
    {
        var tag = new TagTransport(true);
        await using var connection = new ChameleonConnection(tag);
        await connection.OpenAsync();
        await Assert.ThrowsAsync<ChameleonException>(() => new ChameleonClient(connection)
            .WriteLfToT55xxAsync(TagSpecificType.FarpointePyramid, Frame));
        Assert.Empty(tag.AcceptedBlocks);
        Assert.Equal(0x12345678u, tag.Memory[7]);
    }

    [Fact]
    public async Task InvalidFrameOrPasswordSendsNoBlockWrites()
    {
        var tag = new TagTransport(false);
        await using var connection = new ChameleonConnection(tag);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        await Assert.ThrowsAsync<ArgumentException>(() => client.WriteLfToT55xxAsync(
            TagSpecificType.FarpointePyramid, new byte[15]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.WriteLfToT55xxAsync(
            TagSpecificType.FarpointePyramid, Frame, oldKeys: [new byte[3]]));
        Assert.DoesNotContain(tag.Requests, r => r.Command == Command.LfT55xxWrite);
        Assert.True(TagSpecificType.FarpointePyramid.CanWriteToTag());
    }

    private sealed class TagTransport : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        public uint[] Memory { get; } = new uint[8];
        public List<int> AcceptedBlocks { get; } = [];
        public List<ChameleonFrame> Requests { get; } = [];
        public bool MissingRead { get; init; }
        public bool CorruptRead { get; init; }
        public TagTransport(bool protectedTag)
        {
            Memory[0] = protectedTag ? 0x00148050u : 0x00148040u;
            Memory[7] = 0x12345678;
        }
        public byte[] ReadFrame()
        {
            var data = new byte[16];
            for (var i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(i * 4), Memory[i + 1]);
            return data;
        }
        public TransportKind Kind => TransportKind.Tcp;
        public string DisplayName => "T5577 model";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            foreach (var request in _parser.Push(bytes))
            {
                Requests.Add(request);
                var status = Status.Success;
                byte[] payload = [];
                if (request.Command == Command.LfT55xxWrite)
                {
                    var d = request.Data;
                    Assert.Equal(11, d.Length);
                    Assert.Equal(0, d[10]); // page 0, no lock bits
                    var protectedTag = (Memory[0] & 0x10) != 0;
                    if (protectedTag == (d[5] != 0) && (!protectedTag ||
                        BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(6, 4)) == Memory[7]))
                    {
                        Memory[d[0]] = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(1, 4));
                        AcceptedBlocks.Add(d[0]);
                    }
                    status = Status.LfTagOk; // No write ACK, even for rejected writes.
                }
                if (request.Command == Command.PyramidScan)
                {
                    status = MissingRead ? Status.LfTagNoFound : Status.LfTagOk;
                    payload = MissingRead ? [] : ReadFrame();
                    if (CorruptRead) payload[15] ^= 1;
                }
                _replies.Writer.TryWrite(FrameCodec.Encode(request.Command, payload, (ushort)status));
            }
            return Task.CompletedTask;
        }
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var reply = await _replies.Reader.ReadAsync(ct);
            reply.CopyTo(buffer);
            return reply.Length;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
