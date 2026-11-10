using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace ChameLab.Relay;

/// <summary>Limits that keep one relay usable for everyone.</summary>
public sealed class RelayOptions
{
    /// <summary>Devices shared at once; further shares are refused until one ends.</summary>
    public int MaxSessions { get; init; } = 200;

    /// <summary>Join attempts per client IP per minute: a code can't be guessed by trying them all.</summary>
    public int JoinsPerMinute { get; init; } = 10;

    /// <summary>How often idle connections are pinged so a dead one is noticed and dropped.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Endpoints:
/// <list type="bullet">
/// <item><c>/share</c>: a WebSocket for the ChameLab that shares its device. The relay sends it
/// <c>{"type":"code","code":"…"}</c>, then <c>peer-joined</c>/<c>peer-left</c> as the other side
/// comes and goes; binary messages carry the device's bytes.</item>
/// <item><c>/join/{code}</c>: a WebSocket for the ChameLab that connects. On success the relay
/// sends <c>{"type":"joined"}</c>; otherwise it closes with the reason <c>invalid-code</c> or
/// <c>busy</c>, and with <c>host-left</c> when the sharing side goes away.</item>
/// <item><c>/healthz</c>: for monitoring.</item>
/// </list>
/// </summary>
public static class RelayApp
{
    public static WebApplication Build(WebApplicationBuilder builder, RelayOptions? options = null)
    {
        options ??= new RelayOptions();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<RelaySessions>();
        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy("join", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.JoinsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                }));
        });

        var app = builder.Build();
        // The proxy on the same host passes the client's address in X-Forwarded-For; without it
        // every client would share the proxy's address and one rate-limit bucket.
        app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor });
        // A peer that stops answering pings (or a close) is dropped instead of holding its share.
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = options.KeepAlive, KeepAliveTimeout = options.KeepAlive });
        app.UseRateLimiter();

        var sessions = app.Services.GetRequiredService<RelaySessions>();
        app.Map("/healthz", ctx => ctx.Response.WriteAsync("ok"));
        app.Map("/share", sessions.ShareAsync);
        app.Map("/join/{code}", sessions.JoinAsync).RequireRateLimiting("join");
        return app;
    }
}
