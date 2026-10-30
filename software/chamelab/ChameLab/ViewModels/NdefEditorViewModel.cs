using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows.Input;
using ChameLab.Models;
using ChameLab.Protocol;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>What an NDEF record is edited as.</summary>
public enum NdefRecordKind
{
    Link,
    Text,
    Wifi,
    AndroidApp,
    Mime,
    Raw,
}

public static class NdefRecordKindExtensions
{
    public static string Label(this NdefRecordKind k) => k switch
    {
        NdefRecordKind.Link => "Link (URI)",
        NdefRecordKind.Text => "Text",
        NdefRecordKind.Wifi => "Wi-Fi network",
        NdefRecordKind.AndroidApp => "Android app",
        NdefRecordKind.Mime => "MIME data",
        _ => "Raw record",
    };

    public static string Label(this NdefTnf t) => t switch
    {
        NdefTnf.Empty => "Empty",
        NdefTnf.WellKnown => "Well-known (NFC RTD)",
        NdefTnf.Media => "MIME media type",
        NdefTnf.AbsoluteUri => "Absolute URI",
        NdefTnf.External => "External type",
        _ => "Unknown",
    };
}

/// <summary>One line of the record list: what the record is and what it holds.</summary>
public sealed record NdefRecordRow(string Kind, string Value, string Detail)
{
    public bool HasValue => Value.Length > 0;
}

/// <summary>
/// NDEF view and editor of an Ultralight / NTAG card's memory: lists the records of the message
/// on the card, and edits a working copy that <see cref="ApplyCommand"/> writes back into the
/// pages (as an ordinary, undoable memory edit).
/// </summary>
public sealed class NdefEditorViewModel : ViewModelBase
{
    private readonly Func<(IReadOnlyList<byte[]> Pages, TagSpecificType Tag)> _memory;
    private readonly Action<byte[], byte[]?> _write;
    private IReadOnlyList<NdefRecordRow> _records = Array.Empty<NdefRecordRow>();
    private bool _isEditing;

    /// <param name="memory">The card's pages and type as they are now.</param>
    /// <param name="write">Writes a new data area (from page 4) and, when not null, the capability container (page 3).</param>
    public NdefEditorViewModel(
        Func<(IReadOnlyList<byte[]> Pages, TagSpecificType Tag)> memory, Action<byte[], byte[]?> write)
    {
        _memory = memory;
        _write = write;
        EditCommand = ReactiveCommand.Create(BeginEdit);
        CancelCommand = ReactiveCommand.Create(() => { IsEditing = false; Refresh(); });
        ApplyCommand = ReactiveCommand.Create(Apply, this.WhenAnyValue(x => x.CanApply));
        AddCommand = ReactiveCommand.Create<NdefRecordKind>(kind => Add(new NdefRecordEditorViewModel(kind, Validate)));
        RemoveCommand = ReactiveCommand.Create<NdefRecordEditorViewModel>(r => { EditRecords.Remove(r); Validate(); });
        MoveUpCommand = ReactiveCommand.Create<NdefRecordEditorViewModel>(r => Move(r, -1));
        MoveDownCommand = ReactiveCommand.Create<NdefRecordEditorViewModel>(r => Move(r, +1));
    }

    public IReadOnlyList<NdefRecordKind> Kinds { get; } = Enum.GetValues<NdefRecordKind>();

    public IReadOnlyList<NdefRecordRow> Records
    {
        get => _records;
        private set
        {
            this.RaiseAndSetIfChanged(ref _records, value);
            this.RaisePropertyChanged(nameof(HasRecords));
        }
    }

    public bool HasRecords => _records.Count > 0;

    /// <summary>How many records the message has and how much of the card it takes.</summary>
    public string Summary
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>Why the message can't be shown (not formatted, malformed, pages not read); null when fine.</summary>
    public string? Error
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        private set => this.RaiseAndSetIfChanged(ref _isEditing, value);
    }

    public ObservableCollection<NdefRecordEditorViewModel> EditRecords { get; } = new();

    /// <summary>Space the edited message takes, e.g. "58 of 137 bytes".</summary>
    public string Usage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>Why the edited message can't be applied; null when it can.</summary>
    public string? EditError
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool CanApply
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ICommand EditCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }

    /// <summary>Reads the message again from the memory; an edit in progress is left alone.</summary>
    public void Refresh()
    {
        if (_isEditing) return;
        var (pages, tag) = _memory();
        if (!tag.IsUltralight() || pages.Count == 0)
        {
            Records = Array.Empty<NdefRecordRow>();
            Summary = string.Empty;
            Error = null;
            return;
        }
        if (pages.All(p => p.Length == 0))
        {
            // Nothing read yet (a scan without a dump): there is no message to show, empty or not.
            Records = Array.Empty<NdefRecordRow>();
            Summary = "Memory not read yet: dump the card to see its records.";
            Error = null;
            return;
        }
        var area = Type2Ndef.Read(pages, tag);
        Records = area.Records?.Select(r =>
        {
            var (kind, value) = Ndef.Describe(r);
            return new NdefRecordRow(kind, value, $"{r.Tnf.Label()} · {r.TypeText} · {r.Payload.Length} bytes");
        }).ToList() ?? (IReadOnlyList<NdefRecordRow>)Array.Empty<NdefRecordRow>();
        Error = area.Error;
        Summary = area.Records switch
        {
            null => string.Empty,
            { Count: 0 } => $"No NDEF message · {area.Capacity} bytes free",
            var r => $"{r.Count} record{(r.Count == 1 ? "" : "s")} · "
                     + $"{Type2Ndef.UsedBytes(area.PrefixLength, area.MessageLength)} of {area.Capacity} bytes",
        };
    }

    private void BeginEdit()
    {
        var (pages, tag) = _memory();
        EditRecords.Clear();
        foreach (var r in Type2Ndef.Read(pages, tag).Records ?? [])
            EditRecords.Add(NdefRecordEditorViewModel.From(r, Validate));
        IsEditing = true;
        Validate();
    }

    private void Add(NdefRecordEditorViewModel record)
    {
        EditRecords.Add(record);
        Validate();
    }

    private void Move(NdefRecordEditorViewModel record, int by)
    {
        var at = EditRecords.IndexOf(record);
        var to = at + by;
        if (at < 0 || to < 0 || to >= EditRecords.Count) return;
        EditRecords.Move(at, to);
        Validate();
    }

    /// <summary>Checks the edited message after every change: every record valid, and the whole fits the card.</summary>
    private void Validate()
    {
        var (pages, tag) = _memory();
        try
        {
            var message = Ndef.Encode(EditRecords.Select(r => r.Build()).ToList());
            var area = Type2Ndef.Read(pages, tag);
            var prefix = area.IsFormatted ? area.PrefixLength : 0;
            var capacity = area.IsFormatted ? area.Capacity : Type2Ndef.DefaultCapacity(tag);
            var used = Type2Ndef.UsedBytes(prefix, message.Length);
            Usage = $"{used} of {capacity} bytes";
            Type2Ndef.Build(pages, tag, message);
            EditError = null;
            CanApply = true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            // A record's own mistake is shown on that record.
            EditError = EditRecords.Any(r => r.Error is not null) ? null
                : ex is OverflowException ? "A record's type or ID is too long." : ex.Message;
            CanApply = false;
        }
    }

    private void Apply()
    {
        var (pages, tag) = _memory();
        try
        {
            var message = Ndef.Encode(EditRecords.Select(r => r.Build()).ToList());
            var (area, cc) = Type2Ndef.Build(pages, tag, message);
            _write(area, cc);
            IsEditing = false;
            EditRecords.Clear();
            Refresh();
        }
        catch (FormatException ex)
        {
            EditError = ex.Message;
        }
    }
}

/// <summary>One record being edited: the fields of its kind, turned into an <see cref="NdefRecord"/> by <see cref="Build"/>.</summary>
public sealed class NdefRecordEditorViewModel(NdefRecordKind kind, Action changed, NdefRecord? original = null) : ViewModelBase
{
    private readonly byte[] _id = original?.Id ?? [];
    private bool _modified = original is null;
    private NdefRecordKind _kind = kind;
    private string _uri = "https://";
    private string _text = string.Empty;
    private string _language = "en";
    private string _ssid = string.Empty;
    private NdefWifiAuth _wifiAuth = NdefWifiAuth.Wpa2Personal;
    private string _wifiKey = string.Empty;
    private string _package = string.Empty;
    private string _mimeType = "text/plain";
    private string _mimeContent = string.Empty;
    private NdefTnf _rawTnf = NdefTnf.WellKnown;
    private string _rawType = string.Empty;
    private string _rawPayload = string.Empty;

    /// <summary>An editor for a record read from the card, in the kind that shows it best.</summary>
    public static NdefRecordEditorViewModel From(NdefRecord r, Action changed)
    {
        var isUri = Ndef.TryUri(r, out var uri);
        var isText = Ndef.TryText(r, out var text, out var lang);
        var isWifi = Ndef.TryWifi(r, out var wifi);
        var isApp = Ndef.TryAndroidApp(r, out var package);
        var kind = isUri ? NdefRecordKind.Link
            : isText ? NdefRecordKind.Text
            : isWifi ? NdefRecordKind.Wifi
            : isApp ? NdefRecordKind.AndroidApp
            : r.Tnf == NdefTnf.Media && Ndef.PayloadText(r.Payload) is not null ? NdefRecordKind.Mime
            : NdefRecordKind.Raw;
        return new NdefRecordEditorViewModel(kind, changed, r)
        {
            _uri = uri,
            _text = text,
            _language = lang.Length > 0 ? lang : "en",
            _ssid = wifi.Ssid,
            _wifiAuth = wifi.Auth,
            _wifiKey = wifi.Key,
            _package = package,
            _mimeType = r.Tnf == NdefTnf.Media ? r.TypeText : "text/plain",
            _mimeContent = Ndef.PayloadText(r.Payload) ?? string.Empty,
            _rawTnf = r.Tnf is NdefTnf.Unchanged ? NdefTnf.Unknown : r.Tnf,
            _rawType = r.TypeText,
            _rawPayload = Hex.Spaced(r.Payload),
        };
    }

    public IReadOnlyList<NdefRecordKind> Kinds { get; } = Enum.GetValues<NdefRecordKind>();
    public IReadOnlyList<NdefWifiAuth> WifiAuths { get; } = Enum.GetValues<NdefWifiAuth>();
    public IReadOnlyList<NdefTnf> Tnfs { get; } =
        [NdefTnf.WellKnown, NdefTnf.Media, NdefTnf.AbsoluteUri, NdefTnf.External, NdefTnf.Unknown, NdefTnf.Empty];

    public NdefRecordKind Kind
    {
        get => _kind;
        set
        {
            if (value == _kind) return;
            this.RaiseAndSetIfChanged(ref _kind, value);
            this.RaisePropertyChanged(nameof(IsLink));
            this.RaisePropertyChanged(nameof(IsText));
            this.RaisePropertyChanged(nameof(IsWifi));
            this.RaisePropertyChanged(nameof(IsAndroidApp));
            this.RaisePropertyChanged(nameof(IsMime));
            this.RaisePropertyChanged(nameof(IsRaw));
            Edited();
        }
    }

    public bool IsLink => _kind == NdefRecordKind.Link;
    public bool IsText => _kind == NdefRecordKind.Text;
    public bool IsWifi => _kind == NdefRecordKind.Wifi;
    public bool IsAndroidApp => _kind == NdefRecordKind.AndroidApp;
    public bool IsMime => _kind == NdefRecordKind.Mime;
    public bool IsRaw => _kind == NdefRecordKind.Raw;

    public string Uri { get => _uri; set { this.RaiseAndSetIfChanged(ref _uri, value); Edited(); } }
    public string Text { get => _text; set { this.RaiseAndSetIfChanged(ref _text, value); Edited(); } }
    public string Language { get => _language; set { this.RaiseAndSetIfChanged(ref _language, value); Edited(); } }
    public string Ssid { get => _ssid; set { this.RaiseAndSetIfChanged(ref _ssid, value); Edited(); } }

    public NdefWifiAuth WifiAuth
    {
        get => _wifiAuth;
        set
        {
            this.RaiseAndSetIfChanged(ref _wifiAuth, value);
            this.RaisePropertyChanged(nameof(HasWifiKey));
            Edited();
        }
    }

    public bool HasWifiKey => _wifiAuth != NdefWifiAuth.Open;
    public string WifiKey { get => _wifiKey; set { this.RaiseAndSetIfChanged(ref _wifiKey, value); Edited(); } }
    public string Package { get => _package; set { this.RaiseAndSetIfChanged(ref _package, value); Edited(); } }
    public string MimeType { get => _mimeType; set { this.RaiseAndSetIfChanged(ref _mimeType, value); Edited(); } }
    public string MimeContent { get => _mimeContent; set { this.RaiseAndSetIfChanged(ref _mimeContent, value); Edited(); } }
    public NdefTnf RawTnf { get => _rawTnf; set { this.RaiseAndSetIfChanged(ref _rawTnf, value); Edited(); } }
    public string RawType { get => _rawType; set { this.RaiseAndSetIfChanged(ref _rawType, value); Edited(); } }
    public string RawPayload { get => _rawPayload; set { this.RaiseAndSetIfChanged(ref _rawPayload, value); Edited(); } }

    /// <summary>What is wrong with this record's fields; null when it builds.</summary>
    public string? Error
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    private void Edited()
    {
        _modified = true;
        changed();
    }

    /// <summary>
    /// The record as edited; a record from the card that was not touched comes back byte for byte.
    /// Throws <see cref="FormatException"/> (also kept in <see cref="Error"/>) when a field is invalid.
    /// </summary>
    public NdefRecord Build()
    {
        try
        {
            var record = !_modified && original is not null ? original : Create() with { Id = _id };
            Error = null;
            return record;
        }
        catch (FormatException ex)
        {
            Error = ex.Message;
            throw;
        }
    }

    private NdefRecord Create()
    {
        switch (_kind)
        {
            case NdefRecordKind.Link:
                if (string.IsNullOrWhiteSpace(_uri) || _uri.Trim() is "https://" or "http://")
                    throw new FormatException("Enter a link.");
                return Ndef.Uri(_uri.Trim());
            case NdefRecordKind.Text:
                return Ndef.Text(_text, _language);
            case NdefRecordKind.Wifi:
                if (_ssid.Length == 0) throw new FormatException("Enter the network name (SSID).");
                if (Encoding.UTF8.GetByteCount(_ssid) > 32) throw new FormatException("The network name is at most 32 bytes.");
                if (HasWifiKey && _wifiKey.Length is < 8 or > 63)
                    throw new FormatException("A WPA password is 8 to 63 characters.");
                return Ndef.Wifi(new NdefWifi(_ssid, _wifiAuth, HasWifiKey ? _wifiKey : string.Empty));
            case NdefRecordKind.AndroidApp:
                if (string.IsNullOrWhiteSpace(_package) || !_package.Contains('.'))
                    throw new FormatException("Enter the app's package name, e.g. com.example.app.");
                return Ndef.AndroidApp(_package);
            case NdefRecordKind.Mime:
                if (!_mimeType.Contains('/') || _mimeType.Any(c => c > 0x7E || char.IsWhiteSpace(c)))
                    throw new FormatException("Enter a MIME type, e.g. text/plain.");
                return Ndef.Mime(_mimeType, Encoding.UTF8.GetBytes(_mimeContent));
            default:
                if (!Hex.TryParse(_rawPayload, out var payload)) throw new FormatException("The payload must be hex.");
                if (_rawTnf == NdefTnf.Empty && (_rawType.Length > 0 || payload.Length > 0))
                    throw new FormatException("An empty record has no type and no payload.");
                if (_rawTnf is not (NdefTnf.Empty or NdefTnf.Unknown) && _rawType.Length == 0)
                    throw new FormatException("Enter the record type.");
                return new NdefRecord(_rawTnf, Encoding.UTF8.GetBytes(_rawType), [], payload);
        }
    }
}
