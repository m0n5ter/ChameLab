using System.Collections.Generic;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.ViewModels;

/// <summary>
/// Password settings of an Ultralight EV1 / NTAG21x card as the protection panel edits them:
/// the card's own (in its configuration pages) or the ones the writer puts on a target card.
/// </summary>
public interface IProtectionSettings
{
    /// <summary>AUTH0 lies within the memory: pages from it on need the password.</summary>
    bool IsProtected { get; set; }

    /// <summary>PROT: reading needs the password too, not just writing.</summary>
    bool ReadProtected { get; set; }

    /// <summary>PWD as hex; only a complete 4-byte value is taken.</summary>
    string Password { get; set; }

    /// <summary>PACK (the card's answer to the password) as hex; only a complete 2-byte value is taken.</summary>
    string Pack { get; set; }

    /// <summary>First protected page (AUTH0); null while protection is off.</summary>
    decimal? ProtectFrom { get; set; }

    int LastPage { get; }
    IReadOnlyList<AuthLimitOption> AuthLimitOptions { get; }
    AuthLimitOption? SelectedAuthLimit { get; set; }

    /// <summary>One line on what the settings do.</summary>
    string ProtectionSummary { get; }

    /// <summary>Where the settings end up (a slot, a target card); null for none.</summary>
    string? ProtectionNote { get; }
}

/// <summary>An attempt limit choice: the AUTHLIM value and what it allows; a null value is the list's separator.</summary>
public sealed record AuthLimitOption(int? Value, string Label)
{
    public bool IsSeparator => Value is null;

    /// <summary>A real limit: running out of attempts locks the card for good.</summary>
    public bool IsLimited => Value > 0;

    /// <summary>
    /// AUTHLIM choices as wrong attempts allowed: "No limit", a separator, then 1–7 on
    /// Ultralight EV1 or 2–128 on NTAG21x.
    /// </summary>
    public static IReadOnlyList<AuthLimitOption> For(TagSpecificType tag) =>
    [
        new(0, "No limit"),
        new(null, ""),
        .. Enumerable.Range(1, 7).Select(n => new AuthLimitOption(n, UltralightProtection.Attempts(tag, n).ToString())),
    ];
}
