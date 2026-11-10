using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Services;

namespace ChameLab.Transport;

/// <summary>
/// One relay server and the region its codes start with: country letters and a number (TR1).
/// <paramref name="CertSha256"/> pins the relay's self-signed TLS certificate (SHA-256 of the
/// certificate, hex): with it, only that certificate is accepted and no CA is consulted.
/// </summary>
public sealed record RelayServer(string Region, Uri Url, string? CertSha256 = null)
{
    /// <summary>Whether the TLS certificate <paramref name="cert"/> may be trusted for this relay.</summary>
    public bool Trusts(X509Certificate? cert, SslPolicyErrors errors) =>
        CertSha256 is null
            ? errors == SslPolicyErrors.None
            : cert is not null && string.Equals(cert.GetCertHashString(HashAlgorithmName.SHA256), CertSha256,
                StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A share code such as <c>TR1-ABCD-1234</c>: the region of the relay that holds the share, then
/// the 8-character code that relay handed out.
/// </summary>
public readonly record struct ShareCode(string Region, string Code)
{
    public override string ToString() => $"{Region}-{Code[..4]}-{Code[4..]}";

    /// <summary>
    /// Reads a code as typed (any case, with or without dashes and spaces); null when it can't be
    /// one. Look-alike letters in the code part are left to the relay to resolve.
    /// </summary>
    public static ShareCode? Parse(string? text)
    {
        if (text is null) return null;
        var chars = new StringBuilder();
        foreach (var c in text)
        {
            if (c is '-' or ' ') continue;
            if (!char.IsAsciiLetterOrDigit(c)) return null;
            chars.Append(char.ToUpperInvariant(c));
        }
        if (chars.Length != 11 || !char.IsAsciiLetter(chars[0]) || !char.IsAsciiLetter(chars[1])
            || !char.IsAsciiDigit(chars[2]))
            return null;
        var all = chars.ToString();
        return new ShareCode(all[..3], all[3..]);
    }
}

/// <summary>Which relays there are and how they answer.</summary>
public static class RelayEndpoint
{
    /// <summary>
    /// The relays ChameLab knows, pinned to the self-signed certificates ChameLab.Relay/deploy
    /// made on each (redeploying keeps them). The <c>relayServers</c> setting (an object of region → URL)
    /// replaces this list, e.g. to test against a local relay.
    /// </summary>
    public static readonly IReadOnlyList<RelayServer> DefaultServers =
    [
        new("TR1", new Uri("wss://94.73.180.167:7443"), "C8BFABBB72104743B32DCCD86CCF7724D7C1EF4E5D70CEB47282ED0C43536ABE"),
        new("BG1", new Uri("wss://45.39.253.23:7443"), "438D39A6A87237DC6C124748BE5E51AD755B770E7CA101CBCDF6BEB5052286FC"),
        new("DE1", new Uri("wss://45.38.190.118:7443"), "B0FBF452F586A38B279FEC19F4A86D560434FB1432E8E9C30EAF0E29C2DCC7A4"),
        new("RU1", new Uri("wss://185.174.166.100:7443"), "37F0E12B4BBF617F136BDE4074350688D66A6C9545509A4F610CF4D89FD53CC0"),
        new("US1", new Uri("wss://24.144.97.48:7443"), "0D045BBF4CC48761A649784B11AF34D6148D7D7F1CC400F04B5009BE7A359D1C"),
    ];

    public static IReadOnlyList<RelayServer> Servers
    {
        get
        {
            if (AppSettings.Get("relayServers") is not JsonObject configured) return DefaultServers;
            var servers = configured
                .Select(p => p.Value is JsonValue v && v.TryGetValue<string>(out var url)
                             && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    ? new RelayServer(p.Key.ToUpperInvariant(), uri)
                    : null)
                .OfType<RelayServer>()
                .ToList();
            return servers.Count > 0 ? servers : DefaultServers;
        }
    }

    public static RelayServer? Find(string region, IReadOnlyList<RelayServer>? servers = null) =>
        (servers ?? Servers).FirstOrDefault(s => string.Equals(s.Region, region, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The relay with the lowest round trip, timed on its <c>/healthz</c>. Each is asked twice
    /// and the second answer counts: the first also pays for the TCP and TLS handshakes.
    /// </summary>
    public static async Task<RelayServer> PickFastestAsync(IReadOnlyList<RelayServer> servers, CancellationToken ct = default)
    {
        if (servers.Count == 1) return servers[0];
        var times = await Task.WhenAll(servers.Select(s => PingAsync(s, ct)));
        return servers.Zip(times)
                   .Where(p => p.Second is not null)
                   .OrderBy(p => p.Second)
                   .Select(p => p.First)
                   .FirstOrDefault()
               ?? throw new IOException(Strings.Relay_NoServers);
    }

    private static async Task<TimeSpan?> PingAsync(RelayServer server, CancellationToken ct)
    {
        var url = Health(server.Url);
        TimeSpan? last = null;
        using var http = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, cert, _, errors) => server.Trusts(cert, errors) },
        }) { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var watch = Stopwatch.StartNew();
                using var response = await http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) return null;
                last = watch.Elapsed;
            }
            return last;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Console.WriteLine($"[Relay] {server.Region} unreachable: {ex.Message}");
            return null;
        }
    }

    public static Uri Health(Uri relay)
    {
        var http = new UriBuilder(relay)
        {
            Scheme = relay.Scheme == "wss" ? "https" : "http",
            Port = relay.IsDefaultPort ? -1 : relay.Port,
        };
        return new Uri($"{http.Uri.AbsoluteUri.TrimEnd('/')}/healthz");
    }

    /// <summary>A WebSocket for <paramref name="server"/>: keep-alive pings and its pinned certificate.</summary>
    public static ClientWebSocket CreateSocket(RelayServer server)
    {
        var ws = new ClientWebSocket();
        // The browser does both itself: its own keep-alive, and only CA-signed certificates.
        if (OperatingSystem.IsBrowser()) return ws;
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, errors) => server.Trusts(cert, errors);
        return ws;
    }

    public static Uri Share(Uri relay) => new($"{relay.AbsoluteUri.TrimEnd('/')}/share");

    public static Uri Join(Uri relay, string code) =>
        new($"{relay.AbsoluteUri.TrimEnd('/')}/join/{Uri.EscapeDataString(code)}");

    /// <summary>The <c>type</c> of a relay control message, or null when it isn't one.</summary>
    public static string? MessageType(ReadOnlySpan<byte> json) => Property(json, "type");

    /// <summary>A string property of a relay control message, or null.</summary>
    public static string? Property(ReadOnlySpan<byte> json, string name)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using var doc = JsonDocument.ParseValue(ref reader);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(name, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What to tell the user for the reason the relay closed the link with.</summary>
    public static string CloseMessage(string? reason) => reason switch
    {
        "invalid-code" => Strings.Relay_InvalidCode,
        "busy" => Strings.Relay_Busy,
        "host-left" => Strings.Relay_HostLeft,
        _ => Strings.Relay_Closed,
    };
}

/// <summary>Relay "scanner": nothing to discover, it turns a typed code into a candidate.</summary>
public sealed class RelayScanner : ITransportScanner
{
    public TransportKind Kind => TransportKind.Remote;
    public string DisplayName => Strings.Relay_Network;
    public AutoScan AutoScan => AutoScan.Never;

    public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TransportCandidate>>(Array.Empty<TransportCandidate>());

    public ITransport Create(TransportCandidate candidate)
    {
        var (code, server) = Resolve(candidate.Id);
        return new WebSocketTransport(server, code);
    }

    /// <summary>A candidate for a typed code; throws a <see cref="FormatException"/> saying what's wrong with it.</summary>
    public static TransportCandidate Candidate(string typed)
    {
        var (code, server) = Resolve(typed);
        return new TransportCandidate(TransportKind.Remote, code.ToString(),
            string.Format(Strings.Relay_DisplayName, code), server.Url.Host);
    }

    private static (ShareCode Code, RelayServer Server) Resolve(string typed)
    {
        var code = ShareCode.Parse(typed) ?? throw new FormatException(Strings.Relay_InvalidCodeFormat);
        var server = RelayEndpoint.Find(code.Region)
                     ?? throw new FormatException(string.Format(Strings.Relay_UnknownRegion, code.Region));
        return (code, server);
    }
}
