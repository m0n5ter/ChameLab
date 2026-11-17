using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Models;

namespace ChameLab.Services;

/// <summary>What a known dictionary holds, for its title (“MIFARE Classic default keys”).</summary>
public enum KnownDictionaryKind { DefaultKeys, Keys, Passwords }

/// <summary>A key list another project publishes (one hex key per line, <c>#</c> comments).</summary>
/// <param name="Project">Who publishes it: Proxmark3, Flipper Zero, Flipper Unleashed.</param>
/// <param name="Card">The cards its keys are for: MIFARE Classic, T55xx…</param>
public sealed record KnownDictionary(string Project, string Card, KnownDictionaryKind Kind, string FileName, string Url);

/// <summary>
/// The key dictionaries of the Proxmark3 (Iceman), Flipper Zero and Unleashed (a Flipper Zero firmware)
/// projects, downloaded from their repositories on GitHub. The raw files are served with CORS open, so the web app fetches them too.
/// </summary>
public static class KnownDictionaries
{
    public const string Proxmark3 = "Proxmark3";
    public const string FlipperZero = "Flipper Zero";
    public const string Unleashed = "Flipper Unleashed";

    private const string Proxmark3Base = "https://raw.githubusercontent.com/RfidResearchGroup/proxmark3/master/client/dictionaries/";
    private const string FlipperBase = "https://raw.githubusercontent.com/flipperdevices/flipperzero-firmware/dev/applications/main/nfc/resources/nfc/assets/";
    private const string UnleashedBase = "https://raw.githubusercontent.com/DarkFlippers/unleashed-firmware/dev/applications/main/";

    public static IReadOnlyList<KnownDictionary> All { get; } =
    [
        Pm3("MIFARE Classic", KnownDictionaryKind.DefaultKeys, "mfc_default_keys.dic"),
        Pm3("MIFARE Ultralight C", KnownDictionaryKind.DefaultKeys, "mfulc_default_keys.dic"),
        Pm3("MIFARE Plus", KnownDictionaryKind.DefaultKeys, "mfp_default_keys.dic"),
        // Its 24-byte 3K3DES keys are longer than a dictionary holds and are left out.
        Pm3("MIFARE DESFire", KnownDictionaryKind.DefaultKeys, "mfdes_default_keys.dic"),
        Pm3("iCLASS", KnownDictionaryKind.DefaultKeys, "iclass_default_keys.dic"),
        Pm3("iCLASS Elite", KnownDictionaryKind.Keys, "iclass_elite_keys.dic"),
        Pm3("T55xx", KnownDictionaryKind.Passwords, "t55xx_default_pwds.dic"),
        Pm3("Hitag2", KnownDictionaryKind.DefaultKeys, "ht2_default.dic"),
        Flipper("MIFARE Classic", KnownDictionaryKind.DefaultKeys, "mf_classic_dict.nfc"),
        Flipper("MIFARE Ultralight C", KnownDictionaryKind.DefaultKeys, "mf_ultralight_c_dict.nfc"),
        UnleashedNfc("MIFARE Classic", KnownDictionaryKind.DefaultKeys, "mf_classic_dict.nfc"),
        UnleashedNfc("MIFARE Plus", KnownDictionaryKind.DefaultKeys, "mf_plus_dict.nfc"),
        UnleashedNfc("MIFARE Ultralight C", KnownDictionaryKind.DefaultKeys, "mf_ultralight_c_dict.nfc"),
        UnleashedNfc("MIFARE Ultralight AES", KnownDictionaryKind.DefaultKeys, "mf_ultralight_aes_dict.nfc"),
        new(Unleashed, "T5577", KnownDictionaryKind.Passwords, "t5577_dict.lfrfid",
            UnleashedBase + "lfrfid/resources/lfrfid/assets/t5577_dict.lfrfid"),
    ];

    private static KnownDictionary Pm3(string card, KnownDictionaryKind kind, string file) =>
        new(Proxmark3, card, kind, file, Proxmark3Base + file);

    private static KnownDictionary Flipper(string card, KnownDictionaryKind kind, string file) =>
        new(FlipperZero, card, kind, file, FlipperBase + file);

    private static KnownDictionary UnleashedNfc(string card, KnownDictionaryKind kind, string file) =>
        new(Unleashed, card, kind, file, UnleashedBase + "nfc/resources/nfc/assets/" + file);

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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
