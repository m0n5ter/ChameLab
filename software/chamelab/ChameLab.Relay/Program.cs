using ChameLab.Relay;

var builder = WebApplication.CreateSlimBuilder(args);
// TLS straight from Kestrel when configured (deploy/: a self-signed certificate that ChameLab
// pins), e.g. ASPNETCORE_URLS=https://+:8443 with Kestrel__Certificates__Default__Path/KeyPath.
builder.WebHost.UseKestrelHttpsConfiguration();
// Plain HTTP on loopback (behind a TLS proxy) unless ASPNETCORE_URLS says otherwise.
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5080");

// One line per share opened or closed, not per request.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var app = RelayApp.Build(builder);
app.Run();
