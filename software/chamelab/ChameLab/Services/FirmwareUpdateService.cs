using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Transport;

namespace ChameLab.Services;

/// <summary>
/// The newest firmware published on GitHub for one device model. <see cref="Tag"/> is the
/// release's (ChameLab's) version, <see cref="Version"/> the firmware's own.
/// </summary>
public sealed record FirmwareRelease(
    FirmwareVersion Version, string Tag, string Name, string Notes, string PageUrl,
    string? PackageUrl, long PackageSize);

public enum FirmwareUpdateStep { Downloading, Restarting, WaitingForBootloader, Flashing, Reconnecting }

public readonly record struct FirmwareUpdateProgress(FirmwareUpdateStep Step, double Fraction = 0);

/// <summary>
/// Checks GitHub releases for newer firmware and installs a DFU package: restarts
/// the device into its bootloader, flashes it over USB serial or BLE and reconnects.
/// </summary>
public sealed partial class FirmwareUpdateService(DeviceService device, IReadOnlyList<IDfuLocator> locators)
{
    /// <summary>Where releases (with <c>ultra-dfu-app.zip</c> / <c>lite-dfu-app.zip</c>) are published.</summary>
    public const string Repository = "m0n5ter/ChameLab";

    private static readonly TimeSpan BootloaderTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(40);

    private static readonly HttpClient Http = CreateHttp();

    /// <summary>The line releases start with: <c>ChameLab v1.0.7 with firmware v2.3.2-cl</c>.</summary>
    [GeneratedRegex(@"\bfirmware (v\d+\.\d+\S*)", RegexOptions.IgnoreCase)]
    private static partial Regex FirmwareLine();

    /// <summary>Whether firmware can be installed to a device connected over <paramref name="kind"/>.</summary>
    public bool CanInstall(TransportKind? kind) => locators.Any(l => l.Kind == kind);

    /// <summary>Whether a release file is the application package for <paramref name="model"/>: <c>ultra-dfu-app-2.3.2-cl.zip</c>.</summary>
    public static bool IsPackage(string? name, DeviceModel model) =>
        name is not null
        && name.StartsWith(model == DeviceModel.Lite ? "lite-dfu-app" : "ultra-dfu-app", StringComparison.Ordinal)
        && name.EndsWith(".zip", StringComparison.Ordinal);

    /// <summary>The latest (non pre-release) release, with the package for <paramref name="model"/>.</summary>
    public async Task<FirmwareRelease> GetLatestAsync(DeviceModel model, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("No firmware has been released yet.");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var line = FirmwareLine().Match(notes);
        if (!line.Success || !FirmwareVersion.TryParse(line.Groups[1].Value, out var version))
            throw new InvalidOperationException($"Release {tag} names no firmware version.");

        string? url = null;
        long size = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!IsPackage(asset.GetProperty("name").GetString(), model)) continue;
            url = asset.GetProperty("browser_download_url").GetString();
            size = asset.GetProperty("size").GetInt64();
        }

        return new FirmwareRelease(
            version, tag,
            root.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name ? name : tag,
            notes,
            root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repository}/releases",
            url, size);
    }

    public async Task<byte[]> DownloadAsync(FirmwareRelease release, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (release.PackageUrl is null)
            throw new InvalidOperationException($"Release {release.Tag} has no firmware package for this device.");
        using var response = await Http.GetAsync(release.PackageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? release.PackageSize;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new System.IO.MemoryStream();
        var chunk = new byte[16384];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (total > 0) progress?.Report((double)buffer.Length / total);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// The bootloader of the device connected now, to pass to <see cref="InstallAsync"/>
    /// (and again for a retry, when the device already waits in its bootloader).
    /// </summary>
    public IDfuTarget PrepareTarget()
    {
        var device1 = device.Candidate;
        var locator = locators.FirstOrDefault(l => l.Kind == device1?.Kind)
            ?? throw new NotSupportedException("Firmware can be installed over USB from the desktop app, or over Bluetooth from the phone app.");
        return locator.Prepare(device1);
    }

    /// <summary>
    /// Flashes <paramref name="package"/>. A connected device is restarted into its bootloader first;
    /// otherwise a device already waiting in the bootloader (an earlier attempt failed) is flashed.
    /// </summary>
    public async Task InstallAsync(
        DfuPackage package, IDfuTarget target,
        IProgress<FirmwareUpdateProgress> progress, CancellationToken ct = default)
    {
        var previous = device.Candidate;

        if (device.Client is { } client)
        {
            progress.Report(new(FirmwareUpdateStep.Restarting));
            client.EnterBootloader();
            await Task.Delay(300, ct); // let the command go out before the port closes
            await device.DisconnectAsync();
        }

        progress.Report(new(FirmwareUpdateStep.WaitingForBootloader));
        var channel = await PollAsync(() => target.TryOpenAsync(ct), BootloaderTimeout, ct,
            "The device did not show up in its bootloader. Unplug it, hold B while plugging it back in, and retry.");

        progress.Report(new(FirmwareUpdateStep.Flashing));
        await using (channel)
        {
            await new SecureDfu(channel).FlashAsync(package,
                new Progress<double>(f => progress.Report(new(FirmwareUpdateStep.Flashing, f))), ct);
        }

        progress.Report(new(FirmwareUpdateStep.Reconnecting));
        if (previous is null) return;   // flashed a device that was already waiting in its bootloader
        // A port is listed (a peripheral advertises) a moment before it accepts a connection, so keep trying.
        await PollAsync(async () =>
        {
            var found = await device.ScanAsync(previous.Kind, ct);
            if ((found.FirstOrDefault(c => c.Id == previous?.Id) ?? found.FirstOrDefault()) is not { } candidate) return null;
            await device.ConnectAsync(candidate, ct);
            return candidate;
        }, ReconnectTimeout, ct, "The firmware was installed, but the device did not come back. Reconnect it from the Connect page.");
    }

    /// <summary>
    /// Retries <paramref name="probe"/> until it finds something; on timeout the last error it hit
    /// (a connection that failed, say) is appended to <paramref name="timeoutMessage"/>.
    /// </summary>
    private static async Task<T> PollAsync<T>(Func<Task<T?>> probe, TimeSpan timeout, CancellationToken ct, string timeoutMessage) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await probe() is { } found) return found;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { last = ex; /* the port list is in flux while USB re-enumerates */ }
            await Task.Delay(300, ct);
        }
        throw new TimeoutException(last is null ? timeoutMessage : $"{timeoutMessage} (Last error: {last.Message})", last);
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ChameLab");
        return http;
    }
}
