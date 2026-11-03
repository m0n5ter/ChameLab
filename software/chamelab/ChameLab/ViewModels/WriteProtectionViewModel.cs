using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>
/// Password settings the writer puts on the target Ultralight EV1 / NTAG21x card, starting from
/// the source card's and edited without changing it. With protection off the target is left
/// unprotected (AUTH0 = FF), its password still set.
/// </summary>
public sealed class WriteProtectionViewModel(UltralightProtection source, TagSpecificType tag) : ViewModelBase, IProtectionSettings
{
    private UltralightProtection _settings = source;

    // AUTH0 to come back to when protection is switched on again.
    private byte _lastAuth0 = source.IsEnabled(tag) ? source.Auth0 : (byte)Type2Ndef.FirstPage;

    /// <summary>What goes onto the target card.</summary>
    public UltralightProtection Settings => _settings;

    public bool IsProtected
    {
        get => _settings.IsEnabled(tag);
        set
        {
            if (value == IsProtected) return;
            Set(_settings with { Auth0 = value ? (byte)Math.Max(Type2Ndef.FirstPage, (int)_lastAuth0) : UltralightProtection.Off });
        }
    }

    public bool ReadProtected
    {
        get => _settings.ReadProtected;
        set => Set(_settings with { ReadProtected = value });
    }

    public string Password
    {
        get => Hex.Spaced(_settings.Pwd);
        set
        {
            if (Hex.TryParse(value, out var pwd) && pwd.Length == 4) Set(_settings with { Pwd = pwd });
        }
    }

    public string Pack
    {
        get => Hex.Spaced(_settings.Pack);
        set
        {
            if (Hex.TryParse(value, out var pack) && pack.Length == 2) Set(_settings with { Pack = pack });
        }
    }

    public decimal? ProtectFrom
    {
        get => IsProtected ? _settings.Auth0 : null;
        set
        {
            if (value is not { } page || page < 0 || page >= tag.UltralightPages()) return;
            _lastAuth0 = (byte)page;
            Set(_settings with { Auth0 = (byte)page });
        }
    }

    public int LastPage => Math.Max(0, tag.UltralightPages() - 1);

    public IReadOnlyList<AuthLimitOption> AuthLimitOptions { get; } = AuthLimitOption.For(tag);

    public AuthLimitOption? SelectedAuthLimit
    {
        get => AuthLimitOptions.FirstOrDefault(o => o.Value == _settings.AuthLimit);
        set
        {
            if (value?.Value is { } limit) Set(_settings with { AuthLimit = limit });
        }
    }

    public string ProtectionSummary => _settings.Summary(tag);

    public string? ProtectionNote => IsProtected
        ? $"Written after the pages. Keep the password ({Password}): without it the protected pages can't be changed."
        : $"Written after the pages: protection is switched off, the password set to {Password}.";

    private void Set(UltralightProtection next)
    {
        _settings = next;
        this.RaisePropertyChanged(nameof(Settings));
        this.RaisePropertyChanged(nameof(IsProtected));
        this.RaisePropertyChanged(nameof(ReadProtected));
        this.RaisePropertyChanged(nameof(Password));
        this.RaisePropertyChanged(nameof(Pack));
        this.RaisePropertyChanged(nameof(ProtectFrom));
        this.RaisePropertyChanged(nameof(SelectedAuthLimit));
        this.RaisePropertyChanged(nameof(ProtectionSummary));
        this.RaisePropertyChanged(nameof(ProtectionNote));
    }
}
