using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Protocol;
using ChameLab.Transport;
using Xunit;

namespace ChameLab.Tests;

public class Em4305WriteTests
{
    private static readonly byte[] Id = Convert.FromHexString("123456789A");

    [Theory]
    [InlineData(null)]
    [InlineData("CAFEBABE")]
    public async Task SendsIdAndOptionalPasswordInReaderMode(string? password)
    {
        var transport = new Em4305Transport(Status.LfTagOk);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);
        var pwd = password is null ? null : Convert.FromHexString(password);

        await client.WriteEm410xToEm4x05Async(Id, pwd);

        Assert.Equal(new[] { Command.ChangeDeviceMode, Command.Em410xWriteToEm4x05 }, transport.Requests.Select(r => r.Command));
        Assert.Equal(pwd is null ? Id : [.. Id, .. pwd], transport.Requests[1].Data.ToArray());
    }

    [Theory]
    [InlineData(Status.CmdErr)]
    [InlineData(Status.LfTagNoFound)]
    public async Task ReportsFailedWrite(Status status)
    {
        var transport = new Em4305Transport(status);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);

        var e = await Assert.ThrowsAsync<ChameleonException>(() => client.WriteEm410xToEm4x05Async(Id));
        Assert.Equal(status, e.Status);
    }

    [Fact]
    public async Task RejectsBadLengthsWithoutSending()
    {
        var transport = new Em4305Transport(Status.LfTagOk);
        await using var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var client = new ChameleonClient(connection);

        await Assert.ThrowsAsync<ArgumentException>(() => client.WriteEm410xToEm4x05Async(new byte[13]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.WriteEm410xToEm4x05Async(Id, new byte[3]));
        Assert.Empty(transport.Requests);
    }

    private sealed class Em4305Transport(Status writeStatus) : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        public List<ChameleonFrame> Requests { get; } = [];
        public TransportKind Kind => TransportKind.Tcp;
        public string DisplayName => "EM4305 test device";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var request in _parser.Push(data))
            {
                Requests.Add(request);
                var status = request.Command == Command.Em410xWriteToEm4x05 ? writeStatus : Status.Success;
                _replies.Writer.TryWrite(FrameCodec.Encode(request.Command, [], (ushort)status));
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
