using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>Password choices shared by the T55xx tool and LF card writer.</summary>
public sealed class T55xxPasswordSettingsViewModel : ViewModelBase
{
    public T55xxPasswordSettingsViewModel(DictionaryLibrary dictionaries)
    {
        Keys = new(dictionaries);
        Keys.WhenAnyValue(x => x.Count).Subscribe(_ =>
        {
            this.RaisePropertyChanged(nameof(KeyCount));
            this.RaisePropertyChanged(nameof(KeyCountText));
            this.RaisePropertyChanged(nameof(ReadyToStart));
        });
    }

    public T55xxKeySelectionViewModel Keys { get; }

    /// <summary>True when the user already knows the password; false to try a dictionary.</summary>
    public bool KnowKey
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(UseDictionary));
            this.RaisePropertyChanged(nameof(KnownKeyError));
            this.RaisePropertyChanged(nameof(ReadyToStart));
        }
    }

    /// <summary>The inverse of <see cref="KnowKey"/>, for the dictionary-attack radio button.</summary>
    public bool UseDictionary
    {
        get => !KnowKey;
        set => KnowKey = !value;
    }

    /// <summary>The password the user says they know, as hex (used instead of a dictionary).</summary>
    public string KnownKey
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(KnownKeyError));
            this.RaisePropertyChanged(nameof(ReadyToStart));
        }
    } = "";

    // The known password is only required, and only validated, in "I know the password" mode.
    public string? KnownKeyError => !KnowKey || ValidKey(KnownKey) ? null : Strings.Common_Password4Bytes;

    /// <summary>Whether Start has what it needs: a 4-byte known key, or at least one dictionary key.</summary>
    public bool ReadyToStart => KnowKey
        ? Hex.TryParse(KnownKey, out var key) && key.Length == 4
        : KeyCount > 0;

    private enum NewKeyMode { Clear, Set, Keep }
    private NewKeyMode _newKeyMode;

    private void SelectNewKeyMode(NewKeyMode mode)
    {
        if (_newKeyMode == mode) return;
        _newKeyMode = mode;
        this.RaisePropertyChanged(nameof(ClearKey));
        this.RaisePropertyChanged(nameof(SetNewKey));
        this.RaisePropertyChanged(nameof(KeepKey));
        this.RaisePropertyChanged(nameof(NewKeyError));
    }

    public bool ClearKey
    {
        get => _newKeyMode == NewKeyMode.Clear;
        set { if (value) SelectNewKeyMode(NewKeyMode.Clear); }
    }

    /// <summary>Keep the matched password, or leave an unprotected tag unprotected.</summary>
    public bool KeepKey
    {
        get => _newKeyMode == NewKeyMode.Keep;
        set { if (value) SelectNewKeyMode(NewKeyMode.Keep); }
    }

    /// <summary>The user wants to set a new key after a match.</summary>
    public bool SetNewKey
    {
        get => _newKeyMode == NewKeyMode.Set;
        set => SelectNewKeyMode(value ? NewKeyMode.Set :
            SetNewKey ? NewKeyMode.Clear : _newKeyMode);
    }

    public string NewKey
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(NewKeyError));
        }
    } = "";

    /// <summary>The default key used when the user doesn't set one, as hex (shown in place of the input).</summary>
    public string DefaultKeyText { get; } = Hex.Spaced(ChameleonClient.DefaultT55xxKey);

    // The key is only required, and only validated, when the user chose to set one.
    public string? NewKeyError => !SetNewKey || ValidKey(NewKey) ? null : Strings.Common_Password4Bytes;
    private static bool ValidKey(string? text) => string.IsNullOrWhiteSpace(text) ||
        Hex.TryParse(text, out var key) && key.Length == 4;

    public int KeyCount => Keys.Count;
    public string KeyCountText => KeyCount == 0 ? Strings.Cards_NoKeysSelected :
        Loc.Plural(Strings.Plural_UniqueKeys, KeyCount);

    public IReadOnlyList<byte[]> BuildOldKeys() => KnowKey ? [Hex.Parse(KnownKey)] : Keys.BuildKeys();

    public byte[] BuildNewKey() => SetNewKey && !string.IsNullOrWhiteSpace(NewKey)
        ? Hex.Parse(NewKey) : ChameleonClient.DefaultT55xxKey;
}
