using Xunit;
using System.Threading.Channels;
using ChameLab.Protocol;
using ChameLab.Transport;

namespace ChameLab.Cli.Tests;

/// <summary>A session borrowing another owner's link (the app's console) and exclusive use of that link.</summary>
public sealed class HostTests
{
    [Fact]
    public async Task BorrowedLinkStaysOpenAfterExitAndDispose()
    {
        var device = new ScriptedTransport(r => ScriptedTransport.Reply(r, r.Command == Command.GetAppVersion ? [2, 1] : r.Command == Command.GetDeviceModel ? [0] : []));
        await using var connection = new ChameleonConnection(device);
        await connection.OpenAsync();
        var host = new BorrowingHost(connection, Path.GetTempPath());
        using var output = new StringWriter();
        await using (var cli = new CliSession(output, TextWriter.Null, host))
        {
            Assert.True(cli.Connected);
            Assert.Equal(0, await cli.ExecuteAsync("hw version"));
            Assert.Equal(0, await cli.ExecuteAsync("exit"));
            Assert.True(cli.ExitRequested);
        }
        Assert.False(connection.IsClosed);
        Assert.Equal(0, host.Disconnects);
        Assert.Contains(device.Requests, r => r.Command == Command.GetAppVersion);
    }

    [Fact]
    public async Task RelativeFileNamesResolveThroughTheHost()
    {
        var folder = Directory.CreateTempSubdirectory("chamelab-host-").FullName;
        try
        {
            var dump = new byte[1024];
            dump[0] = 0xAB;
            await File.WriteAllBytesAsync(Path.Combine(folder, "card.bin"), dump);
            using var output = new StringWriter();
            using var error = new StringWriter();
            await using var cli = new CliSession(output, error, new BorrowingHost(null, folder));
            Assert.Equal(0, await cli.ExecuteAsync("hf mf view -d card.bin"));
            Assert.Contains("AB", output.ToString());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task ExclusiveUseHoldsOtherRequestsUntilItEnds()
    {
        var device = new ScriptedTransport(r => ScriptedTransport.Reply(r));
        await using var connection = new ChameleonConnection(device);
        await connection.OpenAsync();
        var inside = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var exclusive = connection.RunExclusiveAsync(async () =>
        {
            await connection.SendAsync(Command.GetAppVersion);
            inside.SetResult();
            await release.Task;
            await connection.SendAsync(Command.GetDeviceMode);
        });
        await inside.Task;
        var other = Task.Run(() => connection.SendAsync(Command.GetActiveSlot));
        await Task.Delay(100);
        Assert.False(other.IsCompleted);

        release.SetResult();
        await exclusive;
        await other.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([Command.GetAppVersion, Command.GetDeviceMode, Command.GetActiveSlot], device.Requests.Select(r => r.Command));
    }

    [Fact]
    public async Task ExclusiveUseStartsOnceRequestsInFlightAreAnswered()
    {
        var device = new HeldTransport(Command.GetActiveSlot);
        await using var connection = new ChameleonConnection(device);
        await connection.OpenAsync();

        var pending = connection.SendAsync(Command.GetActiveSlot, timeoutMs: 10_000);
        await device.Held.Task;
        var started = false;
        var exclusive = connection.RunExclusiveAsync(async () =>
        {
            started = true;
            await connection.SendAsync(Command.GetAppVersion);
        });
        await Task.Delay(100);
        Assert.False(started);

        device.Release();
        await pending;
        await exclusive.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(started);
    }

    [Fact]
    public async Task CanceledWaitForExclusiveUseLeavesTheLinkUsable()
    {
        var device = new ScriptedTransport(r => ScriptedTransport.Reply(r));
        await using var connection = new ChameleonConnection(device);
        await connection.OpenAsync();
        var release = new TaskCompletionSource();
        var first = connection.RunExclusiveAsync(() => release.Task);

        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.RunExclusiveAsync(() => Task.CompletedTask, cts.Token));

        release.SetResult();
        await first;
        await connection.SendAsync(Command.GetAppVersion).WaitAsync(TimeSpan.FromSeconds(5));
        await connection.RunExclusiveAsync(() => connection.SendAsync(Command.GetDeviceMode)).WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Lends a link it does not own, and points relative files into <paramref name="folder"/>.</summary>
    private sealed class BorrowingHost(ChameleonConnection? connection, string folder) : CliHost
    {
        public int Disconnects { get; private set; }
        public override ChameleonConnection? Connection => connection;
        public override Task<string> ConnectAsync(string? port, string? code, CancellationToken ct) => throw new NotSupportedException();
        public override Task DisconnectAsync() { Disconnects++; return Task.CompletedTask; }
        public override string ResolvePath(string path) => Path.Combine(folder, path);
    }

    /// <summary>Answers every request at once, except <paramref name="held"/>, which waits for <see cref="Release"/>.</summary>
    private sealed class HeldTransport(Command held) : ITransport
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        private byte[]? _heldReply;
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TransportKind Kind => TransportKind.Serial;
        public string DisplayName => "Held reply fixture";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var request in _parser.Push(data))
            {
                var reply = FrameCodec.Encode(request.Command, [], 0);
                if (request.Command == held) { _heldReply = reply; Held.TrySetResult(); }
                else _incoming.Writer.TryWrite(reply);
            }
            return Task.CompletedTask;
        }

        public void Release() => _incoming.Writer.TryWrite(_heldReply!);

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var frame = await _incoming.Reader.ReadAsync(ct);
            frame.CopyTo(buffer);
            return frame.Length;
        }

        public ValueTask DisposeAsync() { _incoming.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
