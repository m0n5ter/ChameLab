using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using ChameLab.Protocol;
using ChameLab.Relay;
using ChameLab.Services;
using ChameLab.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ChameLab.Cli.Tests;

/// <summary>
/// The CLI's relay commands end to end: <c>hw connect --code</c> joins a shared device, and
/// <c>hw share</c> hands out a code and relays bytes to a local device. A relay runs on a loopback
/// port, pointed at by the process-wide <c>relayServers</c> setting, so these run alone.
/// </summary>
[Collection(SharingTests.Serial)]
public sealed class SharingTests : IDisposable
{
    public const string Serial = "CLI relay settings";

    [CollectionDefinition(Serial, DisableParallelization = true)]
    public sealed class Collection { }

    public void Dispose() => AppSettings.Set("relayServers", null);

    private static void UseRelay(TestRelay relay) =>
        AppSettings.Set("relayServers", new JsonObject { [TestRelay.Region] = relay.Url.ToString() });

    [Fact]
    public async Task ConnectByCode_JoinsASharedDevice()
    {
        await using var relay = await TestRelay.StartAsync();
        UseRelay(relay);

        var host = new DeviceSharingService(new DeviceService([new FixedScanner(new EchoDevice())]));
        await host.StartAsync(FixedScanner.Candidate, [relay.Server], TestRelay.Timeout());
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            await using var cli = new CliSession(output, error);

            Assert.Equal(0, await cli.ExecuteAsync($"hw connect --code {host.Code}", TestRelay.Timeout()));
            Assert.True(cli.Connected);
            Assert.Contains($"connected (shared {host.Code}", output.ToString());
            Assert.Equal("", error.ToString());

            // Commands flow over the shared link.
            Assert.Equal(0, await cli.ExecuteAsync("hw chipid", TestRelay.Timeout()));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task ConnectByCode_RejectsAMalformedCode()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        await using var cli = new CliSession(output, error);

        Assert.Equal(2, await cli.ExecuteAsync("hw connect --code not-a-code"));
        Assert.False(cli.Connected);
        Assert.NotEqual("", error.ToString());
    }

    [Fact]
    public async Task Share_HandsOutACodeAndRelaysToTheDevice()
    {
        await using var relay = await TestRelay.StartAsync();
        UseRelay(relay);
        await using var device = new TcpEchoDevice();

        var output = new LockingWriter();
        var error = new LockingWriter();
        await using var cli = new CliSession(output, error);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var share = cli.ExecuteAsync($"hw share -p tcp://127.0.0.1:{device.Port}", cts.Token);

        var code = ShareCode.Parse(await WaitForCodeAsync(output, share, TestRelay.Timeout()));
        Assert.Equal(TestRelay.Region, code?.Region);

        await using (var peer = new ChameleonConnection(new WebSocketTransport(relay.Server, code!.Value)))
        {
            await peer.OpenAsync(TestRelay.Timeout());
            var reply = await peer.SendAsync(Command.GetAppVersion, [9], ct: TestRelay.Timeout());
            Assert.Equal(Command.GetAppVersion, reply.Command);
            Assert.Equal(new byte[] { 9 }, reply.Data);
        }

        await cts.CancelAsync();
        Assert.Equal(0, await share);
    }

    /// <summary>Waits for <c>hw share</c> to print its code, or fails if the command ends first.</summary>
    private static async Task<string> WaitForCodeAsync(LockingWriter output, Task<int> share, CancellationToken ct)
    {
        while (true)
        {
            var match = Regex.Match(output.Text(), @"Share code: (\S+)");
            if (match.Success) return match.Groups[1].Value;
            Assert.False(share.IsCompleted, "The share ended before printing a code: " + output.Text());
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct);
        }
    }

    /// <summary>A relay on a free loopback port (plain ws, no TLS) for one test.</summary>
    private sealed class TestRelay : IAsyncDisposable
    {
        public const string Region = "XX1";

        private readonly WebApplication _app;

        private TestRelay(WebApplication app, Uri url)
        {
            _app = app;
            Url = url;
            Server = new RelayServer(Region, url);
        }

        public Uri Url { get; }
        public RelayServer Server { get; }

        public static async Task<TestRelay> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.WebHost.UseShutdownTimeout(TimeSpan.FromMilliseconds(200));
            builder.Logging.ClearProviders();
            var app = RelayApp.Build(builder, new RelayOptions { JoinsPerMinute = 1000 });
            await app.StartAsync();
            var http = app.Urls.First();
            return new TestRelay(app, new Uri("ws" + http["http".Length..]));
        }

        public static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>A scanner that always returns the one device it was given.</summary>
    private sealed class FixedScanner(ITransport transport) : ITransportScanner
    {
        public static readonly TransportCandidate Candidate = new(TransportKind.Serial, "COM9", "COM9");
        public TransportKind Kind => TransportKind.Serial;
        public string DisplayName => "Test";

        public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TransportCandidate>>([Candidate]);

        public ITransport Create(TransportCandidate candidate) => transport;
    }

    /// <summary>A device that answers every request with its own data and a success status.</summary>
    private sealed class EchoDevice : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();

        public TransportKind Kind => TransportKind.Serial;
        public string DisplayName => "Echo";
        public bool IsConnected { get; private set; } = true;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var request in _parser.Push(data))
                _replies.Writer.TryWrite(FrameCodec.Encode(request.Command, request.Data, (ushort)Status.Success));
            return Task.CompletedTask;
        }

        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var reply = await _replies.Reader.ReadAsync(ct);
            reply.CopyTo(buffer);
            return reply.Length;
        }

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            _replies.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The echo device over a loopback TCP socket, for <c>hw share -p tcp://…</c>.</summary>
    private sealed class TcpEchoDevice : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public TcpEchoDevice()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(() => ServeAsync(_cts.Token));
        }

        public int Port { get; }

        private async Task ServeAsync(CancellationToken ct)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(ct);
                await using var stream = client.GetStream();
                var parser = new FrameParser();
                var buffer = new byte[4096];
                while (!ct.IsCancellationRequested)
                {
                    var n = await stream.ReadAsync(buffer, ct);
                    if (n == 0) break;
                    foreach (var request in parser.Push(buffer.AsMemory(0, n)))
                        await stream.WriteAsync(FrameCodec.Encode(request.Command, request.Data, (ushort)Status.Success), ct);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { /* shutting down */ }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try { await _loop; } catch { /* already torn down */ }
            _cts.Dispose();
        }
    }

    /// <summary>A thread-safe text sink: <c>hw share</c> prints from a background thread while the test reads.</summary>
    private sealed class LockingWriter : TextWriter
    {
        private readonly StringBuilder _sb = new();

        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { lock (_sb) _sb.Append(value); }
        public override void Write(string? value) { lock (_sb) _sb.Append(value); }
        public override void WriteLine(string? value) { lock (_sb) _sb.Append(value).Append('\n'); }
        public string Text() { lock (_sb) return _sb.ToString(); }
    }
}
