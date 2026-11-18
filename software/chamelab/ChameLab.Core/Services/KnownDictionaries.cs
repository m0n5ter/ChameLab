using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Models;

namespace ChameLab.Services;

/// <summary>A key list another project publishes (one hex key per line, <c>#</c> comments).</summary>
/// <param name="Project">Who publishes it: Proxmark3, Flipper Zero, Flipper Unleashed.</param>
/// <param name="Card">The cards its keys are for: MIFARE Classic, T55xx…</param>
public sealed record KnownDictionary(string Project, string Card, string FileName, string Url)
{
    /// <summary>The downloaded dictionary's name: the project, short, and the cards (“PM3 - MIFARE Classic”).</summary>
    public string Name => $"{KnownDictionaries.ShortName(Project)} - {Card}";
}

/// <summary>
/// The key dictionaries of the Proxmark3 (Iceman), Flipper Zero and Unleashed (a Flipper Zero firmware)
/// projects, downloaded from their repositories on GitHub. The raw files are served with CORS open, so the web app fetches them too.
/// </summary>
public static class KnownDictionaries
{
    public const string Proxmark3 = "Proxmark3";
    public const string FlipperZero = "Flipper Zero";
    public const string Unleashed = "Flipper Unleashed";

    /// <summary>The project's short name in a dictionary's name: PM3, FZ, FZU.</summary>
    public static string ShortName(string project) => project switch
    {
        Proxmark3 => "PM3",
        FlipperZero => "FZ",
        Unleashed => "FZU",
        _ => project,
    };

    private const string Proxmark3Base = "https://raw.githubusercontent.com/RfidResearchGroup/proxmark3/master/client/dictionaries/";
    private const string FlipperBase = "https://raw.githubusercontent.com/flipperdevices/flipperzero-firmware/dev/applications/main/nfc/resources/nfc/assets/";
    private const string UnleashedBase = "https://raw.githubusercontent.com/DarkFlippers/unleashed-firmware/dev/applications/main/";

    public static IReadOnlyList<KnownDictionary> All { get; } =
    [
        Pm3("MIFARE Classic", "mfc_default_keys.dic"),
        Pm3("MIFARE Ultralight C", "mfulc_default_keys.dic"),
        Pm3("MIFARE Plus", "mfp_default_keys.dic"),
        // Its 24-byte 3K3DES keys are longer than a dictionary holds and are left out.
        Pm3("MIFARE DESFire", "mfdes_default_keys.dic"),
        Pm3("iCLASS", "iclass_default_keys.dic"),
        Pm3("iCLASS Elite", "iclass_elite_keys.dic"),
        Pm3("T55xx", "t55xx_default_pwds.dic"),
        Pm3("Hitag2", "ht2_default.dic"),
        Flipper("MIFARE Classic", "mf_classic_dict.nfc"),
        Flipper("MIFARE Ultralight C", "mf_ultralight_c_dict.nfc"),
        UnleashedNfc("MIFARE Classic", "mf_classic_dict.nfc"),
        UnleashedNfc("MIFARE Plus", "mf_plus_dict.nfc"),
        UnleashedNfc("MIFARE Ultralight C", "mf_ultralight_c_dict.nfc"),
        UnleashedNfc("MIFARE Ultralight AES", "mf_ultralight_aes_dict.nfc"),
        new(Unleashed, "T5577", "t5577_dict.lfrfid",
            UnleashedBase + "lfrfid/resources/lfrfid/assets/t5577_dict.lfrfid"),
    ];

    private static KnownDictionary Pm3(string card, string file) =>
        new(Proxmark3, card, file, Proxmark3Base + file);

    private static KnownDictionary Flipper(string card, string file) =>
        new(FlipperZero, card, file, FlipperBase + file);

    private static KnownDictionary UnleashedNfc(string card, string file) =>
        new(Unleashed, card, file, UnleashedBase + "nfc/resources/nfc/assets/" + file);

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // A browser sends its own; a set one makes Safari ask GitHub (which refuses) whether it may, and the
        // download fails with "Load failed".
        if (!OperatingSystem.IsBrowser())
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ChameLab");
        return http;
    }

    /// <summary>Downloads the list and reads its keys (not saved); throws with a user-facing message when it has none.</summary>
    public static async Task<KeyDictionary> DownloadAsync(KnownDictionary source, string name, CancellationToken cancel = default)
    {
        var text = await Http.GetStringAsync(source.Url, cancel).ConfigureAwait(false);
        return Read(source, name, text);
    }

    /// <summary>The keys of a downloaded list.</summary>
    public static KeyDictionary Read(KnownDictionary source, string name, string text)
    {
        var dict = KeyDictionary.FromText(name, text.TrimStart('﻿'));
        if (dict.Count == 0) throw new FormatException(string.Format(Strings.Dictionaries_NoValidKeys, source.FileName));
        return dict;
    }
}
