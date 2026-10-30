using System;
using System.Collections.Generic;
using ChameLab.Protocol;

namespace ChameLab.Models;

/// <summary>
/// Password protection of an Ultralight EV1 / NTAG21x card, as kept in its configuration pages:
/// AUTH0 (first protected page) in CFG0, PROT and AUTHLIM in the ACCESS byte of CFG1, then the
/// PWD and PACK pages.
/// </summary>
public sealed record UltralightProtection(byte Auth0, bool ReadProtected, int AuthLimit, byte[] Pwd, byte[] Pack)
{
    /// <summary>AUTH0 that protects nothing (past the last page of every type).</summary>
    public const byte Off = 0xFF;

    public const byte AccessProt = 0x80;
    public const byte AccessCfgLock = 0x40;
    public const byte AccessAuthLimit = 0x07;

    /// <summary>Some page needs the password: AUTH0 lies within the card's memory.</summary>
    public bool IsEnabled(TagSpecificType tag) => Auth0 < tag.UltralightPages();

    /// <summary>The settings in a dump; null when the type has no password or a configuration page was not read.</summary>
    public static UltralightProtection? FromPages(IReadOnlyList<byte[]> pages, TagSpecificType tag)
    {
        var cfg0 = tag.UltralightCfg0Page();
        if (cfg0 <= 0 || pages.Count < cfg0 + 4) return null;
        for (var p = cfg0; p < cfg0 + 4; p++)
            if (pages[p].Length != 4) return null;
        var access = pages[cfg0 + 1][0];
        return new UltralightProtection(pages[cfg0][3], (access & AccessProt) != 0, access & AccessAuthLimit,
            (byte[])pages[cfg0 + 2].Clone(), pages[cfg0 + 3][..2]);
    }

    /// <summary>The ACCESS byte with PROT and AUTHLIM set from these settings and the other bits kept.</summary>
    public byte Access(byte current) =>
        (byte)((current & ~(AccessProt | AccessAuthLimit)) | (ReadProtected ? AccessProt : 0) | (AuthLimit & AccessAuthLimit));

    /// <summary>One line on what the settings do, e.g. "Pages 4 on need the password to be written and read".</summary>
    public string Summary(TagSpecificType tag) =>
        !IsEnabled(tag) ? "No page is protected."
        : $"Pages {Auth0} on need the password to be {(ReadProtected ? "written and read" : "written")}"
          + (AuthLimit == 0 ? "." : $"; {Attempts(tag, AuthLimit)} wrong attempts lock the card for good.");

    /// <summary>Wrong passwords an AUTHLIM allows: the value itself on Ultralight EV1, 2^AUTHLIM on NTAG21x.</summary>
    public static int Attempts(TagSpecificType tag, int authLimit) =>
        tag is TagSpecificType.Mf0Ul11 or TagSpecificType.Mf0Ul21 ? authLimit : 1 << authLimit;
}
