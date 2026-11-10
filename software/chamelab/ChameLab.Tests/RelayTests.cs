using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Protocol;
using ChameLab.Relay;
using ChameLab.Services;
using ChameLab.Transport;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ChameLab.Tests;

/// <summary>A relay on a free local port for one test.</summary>
internal sealed class TestRelay : IAsyncDisposable
{
    /// <summary>The region test codes start with.</summary>
    public const string Region = "XX1";

    private readonly WebApplication _app;

    private TestRelay(WebApplication app, Uri url, string? certSha256)
    {
        _app = app;
        Url = url;
        Server = new RelayServer(Region, url, certSha256);
    }

    public Uri Url { get; }

    /// <summary>This relay as ChameLab knows it, pinned to its certificate when it has one.</summary>
    public RelayServer Server { get; }

    /// <summary>Starts a relay; with <paramref name="cert"/> it serves TLS itself, as deployed.</summary>
    public static async Task<TestRelay> StartAsync(RelayOptions? options = null, X509Certificate2? cert = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        if (cert is null)
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        }
        else
        {
            builder.WebHost.UseKestrelHttpsConfiguration();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(cert)));
        }
        // Don't wait for sockets a test left open.
        builder.WebHost.UseShutdownTimeout(TimeSpan.FromMilliseconds(200));
        builder.Logging.ClearProviders();
        var app = RelayApp.Build(builder, options ?? new RelayOptions { JoinsPerMinute = 1000 });
        await app.StartAsync();
        var http = app.Urls.First();
        return new TestRelay(app, new Uri("ws" + http["http".Length..]),
            cert?.GetCertHashString(HashAlgorithmName.SHA256));
    }

    /// <summary>A self-signed certificate like the one the deploy script makes on each server.</summary>
    public static X509Certificate2 SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=chamelab-relay", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // SslStream on Windows needs the key in a store-backed form, not the ephemeral one.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    /// <summary>A sharing side speaking the relay's protocol directly; returns it and its code.</summary>
    public async Task<(ClientWebSocket Host, ShareCode Code)> ShareAsync()
    {
        var host = new ClientWebSocket();
        await host.ConnectAsync(RelayEndpoint.Share(Url), Timeout());
        var (type, text) = await ReceiveAsync(host);
        Assert.Equal(WebSocketMessageType.Text, type);
        var code = ShareCode.Parse(Region + RelayEndpoint.Property(Encoding.UTF8.GetBytes(text), "code"));
        Assert.NotNull(code);
        return (host, code.Value);
    }

    public static async Task<(WebSocketMessageType Type, string Text)> ReceiveAsync(WebSocket ws)
    {
        var buffer = new byte[4096];
        var r = await ws.ReceiveAsync(buffer, Timeout());
        return (r.MessageType, r.MessageType == WebSocketMessageType.Text
            ? Encoding.UTF8.GetString(buffer, 0, r.Count)
            : Convert.ToHexString(buffer, 0, r.Count));
    }

    public static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public sealed class RelayTests
{
    private static readonly ShareCode Unknown = new(TestRelay.Region, "ABCD1234");

    [Fact]
    public async Task Join_RelaysBytesBothWays()
    {
        await using var relay = await TestRelay.StartAsync();
        var (host, code) = await relay.ShareAsync();
        using var _ = host;
        await using var peer = new WebSocketTransport(relay.Server, code);

        await peer.ConnectAsync(TestRelay.Timeout());
        await AssertPeerJoinedAsync(host);

        await peer.WriteAsync(new byte[] { 1, 2, 3 }, TestRelay.Timeout());
        Assert.Equal((WebSocketMessageType.Binary, "010203"), await TestRelay.ReceiveAsync(host));

        await host.SendAsync(new byte[] { 4, 5 }, WebSocketMessageType.Binary, true, TestRelay.Timeout());
        var buffer = new byte[16];
        var n = await peer.ReadAsync(buffer, TestRelay.Timeout());
        Assert.Equal("0405", Convert.ToHexString(buffer, 0, n));
    }

    /// <summary>The sharing side hears who joined: this machine's user@computer.</summary>
    private static async Task AssertPeerJoinedAsync(WebSocket host)
    {
        var (type, text) = await TestRelay.ReceiveAsync(host);
        Assert.Equal(WebSocketMessageType.Text, type);
        var json = Encoding.UTF8.GetBytes(text);
        Assert.Equal("peer-joined", RelayEndpoint.MessageType(json));
        Assert.Equal(RelayEndpoint.ClientName, RelayEndpoint.Property(json, "name"));
    }

    [Theory]
    [InlineData("alice@PC", "alice@PC")]
    [InlineData("  bob\u0007@LAPTOP\t ", "bob@LAPTOP")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void PeerName_IsOneShortLine(string? sent, string? shown) =>
        Assert.Equal(shown, RelaySessions.PeerName(sent));

    [Fact]
    public void PeerName_IsCut()
    {
        Assert.Equal(64, RelaySessions.PeerName(new string('x', 200))!.Length);
    }

    [Fact]
    public async Task Join_UnknownCode_SaysNoDeviceIsShared()
    {
        await using var relay = await TestRelay.StartAsync();
        await using var peer = new WebSocketTransport(relay.Server, Unknown);

        var error = await Assert.ThrowsAsync<IOException>(() => peer.ConnectAsync(TestRelay.Timeout()));
        Assert.Equal(Strings.Relay_InvalidCode, error.Message);
    }

    [Fact]
    public async Task Join_WhileSomeoneIsConnected_IsBusy()
    {
        await using var relay = await TestRelay.StartAsync();
        var (host, code) = await relay.ShareAsync();
        using var _ = host;
        await using var first = new WebSocketTransport(relay.Server, code);
        await first.ConnectAsync(TestRelay.Timeout());
        await using var second = new WebSocketTransport(relay.Server, code);

        var error = await Assert.ThrowsAsync<IOException>(() => second.ConnectAsync(TestRelay.Timeout()));
        Assert.Equal(Strings.Relay_Busy, error.Message);
    }

    [Fact]
    public async Task Join_AfterTheLastOneLeft_Works()
    {
        await using var relay = await TestRelay.StartAsync();
        var (host, code) = await relay.ShareAsync();
        using var _ = host;
        var first = new WebSocketTransport(relay.Server, code);
        await first.ConnectAsync(TestRelay.Timeout());
        await TestRelay.ReceiveAsync(host);
        await first.DisposeAsync();
        Assert.Equal((WebSocketMessageType.Text, """{"type":"peer-left"}"""), await TestRelay.ReceiveAsync(host));

        await using var second = new WebSocketTransport(relay.Server, code);
        await second.ConnectAsync(TestRelay.Timeout());
        await AssertPeerJoinedAsync(host);
    }

    [Fact]
    public async Task SharingSideLeaving_DisconnectsThePeerWithAReason()
    {
        await using var relay = await TestRelay.StartAsync();
        var (host, code) = await relay.ShareAsync();
        await using var peer = new WebSocketTransport(relay.Server, code);
        await peer.ConnectAsync(TestRelay.Timeout());

        await host.CloseAsync(WebSocketCloseStatus.NormalClosure, null, TestRelay.Timeout());
        host.Dispose();

        var error = await Assert.ThrowsAsync<IOException>(() => peer.ReadAsync(new byte[16], TestRelay.Timeout()));
        Assert.Equal(Strings.Relay_HostLeft, error.Message);

        // The code died with the share.
        await using var late = new WebSocketTransport(relay.Server, code);
        var lateError = await Assert.ThrowsAsync<IOException>(() => late.ConnectAsync(TestRelay.Timeout()));
        Assert.Equal(Strings.Relay_InvalidCode, lateError.Message);
    }

    [Fact]
    public async Task Joins_AreRateLimitedPerClient()
    {
        await using var relay = await TestRelay.StartAsync(new RelayOptions { JoinsPerMinute = 2 });

        for (var i = 0; i < 2; i++)
        {
            await using var guess = new WebSocketTransport(relay.Server, Unknown);
            var wrong = await Assert.ThrowsAsync<IOException>(() => guess.ConnectAsync(TestRelay.Timeout()));
            Assert.Equal(Strings.Relay_InvalidCode, wrong.Message);
        }

        await using var limited = new WebSocketTransport(relay.Server, Unknown);
        var error = await Assert.ThrowsAsync<IOException>(() => limited.ConnectAsync(TestRelay.Timeout()));
        Assert.Equal(string.Format(Strings.Relay_Unreachable, relay.Url.Host), error.Message);
    }

    [Fact]
    public async Task FastestRelay_IsPicked_AndDeadOnesSkipped()
    {
        await using var relay = await TestRelay.StartAsync();
        var dead = new RelayServer("DD1", new Uri("ws://127.0.0.1:1"));

        var picked = await RelayEndpoint.PickFastestAsync([dead, relay.Server, dead with { Region = "DD2" }]);

        Assert.Equal(relay.Server, picked);
        await Assert.ThrowsAsync<IOException>(() => RelayEndpoint.PickFastestAsync([dead, dead with { Region = "DD2" }]));
    }

    [Fact]
    public async Task PinnedCertificate_IsTrusted_AndNoOtherOne()
    {
        using var cert = TestRelay.SelfSigned();
        await using var relay = await TestRelay.StartAsync(cert: cert);
        Assert.Equal("wss", relay.Url.Scheme);

        // The TLS handshake passes: the relay itself answers that the code is unknown.
        await using var pinned = new WebSocketTransport(relay.Server, Unknown);
        var refused = await Assert.ThrowsAsync<IOException>(() => pinned.ConnectAsync(TestRelay.Timeout()));
        Assert.Equal(Strings.Relay_InvalidCode, refused.Message);
        Assert.Equal(relay.Server, await RelayEndpoint.PickFastestAsync(
            [relay.Server, new RelayServer("DD1", new Uri("wss://127.0.0.1:1"))]));

        using var other = TestRelay.SelfSigned();
        var impostor = relay.Server with { CertSha256 = other.GetCertHashString(HashAlgorithmName.SHA256) };
        await using var wrongPin = new WebSocketTransport(impostor, Unknown);
        var rejected = await Assert.ThrowsAsync<IOException>(() => wrongPin.ConnectAsync(TestRelay.Timeout()));
        Assert.Equal(string.Format(Strings.Relay_Unreachable, relay.Url.Host), rejected.Message);

        // Without a pin a self-signed certificate is not trusted either.
        await using var unpinned = new WebSocketTransport(relay.Server with { CertSha256 = null }, Unknown);
        await Assert.ThrowsAsync<IOException>(() => unpinned.ConnectAsync(TestRelay.Timeout()));
    }

    [Theory]
    [InlineData("ABCD-1234", "ABCD1234")]
    [InlineData("abcd 1234", "ABCD1234")]
    [InlineData("OILA-1234", "011A1234")]
    [InlineData("ABCD-123", null)]
    [InlineData("ABCD-12345", null)]
    [InlineData("ABCU-1234", null)]
    public void RelayCode_ReadsWhatPeopleType(string typed, string? expected) =>
        Assert.Equal(expected, RelayCode.Normalize(typed));

    [Fact]
    public void NewCodes_AreValidAndDistinct()
    {
        var codes = Enumerable.Range(0, 1000).Select(_ => RelayCode.New()).ToList();
        Assert.All(codes, c => Assert.Equal(c, RelayCode.Normalize(RelayCode.Format(c))));
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Theory]
    [InlineData("TR1-ABCD-1234", "TR1", "ABCD1234")]
    [InlineData("tr1 abcd 1234", "TR1", "ABCD1234")]
    [InlineData("US2ABCD1234", "US2", "ABCD1234")]
    [InlineData("TR-ABCD-1234", null, null)]
    [InlineData("T11-ABCD-1234", null, null)]
    [InlineData("TR1-ABCD-123", null, null)]
    [InlineData("TR1-ABCD-12#4", null, null)]
    public void ShareCode_ReadsRegionAndCode(string typed, string? region, string? code)
    {
        var parsed = ShareCode.Parse(typed);
        Assert.Equal(region, parsed?.Region);
        Assert.Equal(code, parsed?.Code);
        if (parsed is { } p) Assert.Equal($"{region}-{code![..4]}-{code[4..]}", p.ToString());
    }
}

public sealed class DeviceSharingServiceTests
{
    [Fact]
    public async Task SharedDevice_AnswersCommandsFromTheOtherSide()
    {
        await using var relay = await TestRelay.StartAsync();
        var device = new EchoDevice();
        var sharing = new DeviceSharingService(new DeviceService([new FixedScanner(device)]));

        await sharing.StartAsync(FixedScanner.Candidate, [relay.Server], TestRelay.Timeout());
        Assert.Equal(SharingState.WaitingForPeer, sharing.State);
        var code = ShareCode.Parse(sharing.Code);
        Assert.Equal(TestRelay.Region, code?.Region);

        await using var conn = new ChameleonConnection(new WebSocketTransport(relay.Server, code!.Value));
        await conn.OpenAsync(TestRelay.Timeout());
        await WaitForAsync(() => sharing.State == SharingState.PeerConnected);
        Assert.Equal(RelayEndpoint.ClientName, sharing.PeerName);

        var reply = await conn.SendAsync(Command.GetAppVersion, [7], ct: TestRelay.Timeout());
        Assert.Equal(Command.GetAppVersion, reply.Command);
        Assert.Equal(new byte[] { 7 }, reply.Data);
        // One 11-byte frame each way (10 bytes of framing, 1 of data).
        Assert.Equal(11, sharing.BytesToDevice);
        Assert.Equal(11, sharing.BytesFromDevice);

        var dropped = new TaskCompletionSource();
        conn.Disconnected += _ => dropped.TrySetResult();
        await sharing.StopAsync();

        Assert.Equal(SharingState.Idle, sharing.State);
        Assert.Null(sharing.Error);
        Assert.Null(sharing.Code);
        Assert.True(device.Disposed);
        await dropped.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task UnpluggedDevice_EndsTheShareWithAReason()
    {
        await using var relay = await TestRelay.StartAsync();
        var device = new EchoDevice();
        var sharing = new DeviceSharingService(new DeviceService([new FixedScanner(device)]));
        await sharing.StartAsync(FixedScanner.Candidate, [relay.Server], TestRelay.Timeout());

        device.Unplug();

        await WaitForAsync(() => sharing.State == SharingState.Idle);
        Assert.Equal(Strings.Share_DeviceLost, sharing.Error);
    }

    [Fact]
    public async Task UnreachableRelay_LeavesTheDeviceFree()
    {
        var device = new EchoDevice();
        var sharing = new DeviceSharingService(new DeviceService([new FixedScanner(device)]));

        await Assert.ThrowsAsync<IOException>(() => sharing.StartAsync(FixedScanner.Candidate,
            [new RelayServer("DD1", new Uri("ws://127.0.0.1:1"))], TestRelay.Timeout()));

        Assert.Equal(SharingState.Idle, sharing.State);
        Assert.True(device.Disposed);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the share state.");
            await Task.Delay(20);
        }
    }

    private sealed class FixedScanner(ITransport transport) : ITransportScanner
    {
        public static readonly TransportCandidate Candidate = new(TransportKind.Serial, "COM9", "COM9");
        public TransportKind Kind => TransportKind.Serial;
        public string DisplayName => "Test";

        public Task<System.Collections.Generic.IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult<System.Collections.Generic.IReadOnlyList<TransportCandidate>>([Candidate]);

        public ITransport Create(TransportCandidate candidate) => transport;
    }

    /// <summary>A device that answers every request with its own data.</summary>
    private sealed class EchoDevice : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();

        public bool Disposed { get; private set; }
        public TransportKind Kind => TransportKind.Serial;
        public string DisplayName => "Echo";
        public bool IsConnected => !Disposed;

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

        public void Unplug() => _replies.Writer.TryComplete(new IOException("unplugged"));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _replies.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
