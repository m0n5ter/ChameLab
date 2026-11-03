using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows.Input;
using ChameLab.Behaviors;
using ChameLab.Models;
using ChameLab.Protocol;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>What an NDEF record is edited as.</summary>
public enum NdefRecordKind
{
    Link,
    Phone,
    Email,
    Sms,
    Geo,
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
        NdefRecordKind.Phone => "Phone number",
        NdefRecordKind.Email => "Email",
        NdefRecordKind.Sms => "Text message (SMS)",
        NdefRecordKind.Geo => "Location",
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

/// <summary>
/// NDEF editor of an Ultralight / NTAG card's memory: the records of the message on the card,
/// edited in place. Every valid change goes straight into the pages (as an ordinary, undoable
/// memory edit); a change to the memory made elsewhere (hex editor, dump) reloads the list.
/// </summary>
public sealed class NdefEditorViewModel : ViewModelBase, IReorderableList
{
    private readonly Func<(IReadOnlyList<byte[]> Pages, TagSpecificType Tag)> _memory;
    private readonly Action<byte[], byte[]?> _write;
    // Set while this editor writes the memory, so the change it causes doesn't reload the list.
    private bool _writing;

    /// <param name="memory">The card's pages and type as they are now.</param>
    /// <param name="write">Writes a new data area (from page 4) and, when not null, the capability container (page 3).</param>
    public NdefEditorViewModel(
        Func<(IReadOnlyList<byte[]> Pages, TagSpecificType Tag)> memory, Action<byte[], byte[]?> write)
    {
        _memory = memory;
        _write = write;
        AddCommand = ReactiveCommand.Create<NdefRecordKind>(kind =>
        {
            Records.Add(new NdefRecordEditorViewModel(kind, RecordsEdited));
            RecordsEdited();
        });
        RemoveCommand = ReactiveCommand.Create<NdefRecordEditorViewModel>(r =>
        {
            Records.Remove(r);
            RecordsEdited();
        });
        Records.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(HasRecords));
            this.RaisePropertyChanged(nameof(CanReorder));
        };
    }

    public ObservableCollection<NdefRecordEditorViewModel> Records { get; } = new();

    public bool HasRecords => Records.Count > 0;

    /// <summary>The memory holds a message area to edit: read, of an Ultralight / NTAG card.</summary>
    public bool CanEdit
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>How many records there are, or why there is no message yet.</summary>
    public string Summary
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>Bytes the records take on the card and what is left, e.g. "58 of 137 bytes · 79 left".</summary>
    public string Usage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>The records take more bytes than the card holds, so they can't go into the memory.</summary>
    public bool IsOverCapacity
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Why the message on the card can't be read (not formatted, malformed); null when fine.</summary>
    public string? Error
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Why the edited records can't go into the memory (too long for the card); null when they can.</summary>
    public string? EditError
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }

    /// <summary>Reads the message again from the memory, unless this editor just wrote it.</summary>
    public void Refresh()
    {
        var (pages, tag) = _memory();
        if (!tag.IsUltralight() || pages.Count == 0 || pages.All(p => p.Length == 0))
        {
            // Nothing read yet (a scan without a dump, a card typed in): no message to show, but
            // records added here start a new one.
            if (_writing) return;
            Records.Clear();
            CanEdit = tag.IsUltralight() && pages.Count > 0;
            Summary = CanEdit ? "Memory not read yet: read the card, or add records to start a new message." : string.Empty;
            Usage = string.Empty;
            IsOverCapacity = false;
            Error = EditError = null;
            return;
        }
        var area = Type2Ndef.Read(pages, tag);
        CanEdit = true;
        Error = area.Error;
        if (_writing) return;
        Records.Clear();
        foreach (var r in area.Records ?? [])
            Records.Add(NdefRecordEditorViewModel.From(r, RecordsEdited));
        EditError = null;
        Count(area.Records is null ? null : area.MessageLength);
    }

    /// <summary>Updates the record count and the bytes taken for a message of <paramref name="messageLength"/> bytes (null: none to count).</summary>
    private void Count(int? messageLength)
    {
        Summary = Records.Count switch
        {
            0 => "No records",
            1 => "1 record",
            var n => $"{n} records",
        };
        var (pages, tag) = _memory();
        var area = Type2Ndef.Read(pages, tag);
        var prefix = area.IsFormatted ? area.PrefixLength : 0;
        var capacity = area.IsFormatted ? area.Capacity : Type2Ndef.DefaultCapacity(tag);
        if (messageLength is not { } length)
        {
            Usage = string.Empty;
            IsOverCapacity = false;
            return;
        }
        var used = Type2Ndef.UsedBytes(prefix, length);
        IsOverCapacity = used > capacity;
        Usage = IsOverCapacity
            ? $"{used} of {capacity} bytes · {used - capacity} over"
            : $"{used} of {capacity} bytes · {capacity - used} left";
    }

    /// <summary>
    /// After every edit: puts the records into the memory once all are valid and fit the card;
    /// otherwise the memory keeps the last valid message and the mistake is shown.
    /// </summary>
    private void RecordsEdited()
    {
        var (pages, tag) = _memory();
        // The bytes are counted over the records that build; a record's own mistake is shown on it.
        var built = new List<NdefRecord>();
        var valid = true;
        foreach (var record in Records)
        {
            try { built.Add(record.Build()); }
            catch (FormatException) { valid = false; }
        }
        byte[] message;
        try { message = Ndef.Encode(built); }
        catch (OverflowException)
        {
            EditError = "A record's type or ID is too long.";
            return;
        }
        Count(message.Length);
        if (!valid || IsOverCapacity)
        {
            EditError = null;
            return;
        }
        try
        {
            var (area, cc) = Type2Ndef.Build(pages, tag, message);
            EditError = null;
            _writing = true;
            try { _write(area, cc); }
            finally { _writing = false; }
        }
        catch (FormatException ex)
        {
            EditError = ex.Message;
        }
    }

    // ---- Dragging records into place (ListReorder) ----

    public bool CanReorder => Records.Count > 1;
    public System.Collections.IList ReorderItems => Records;
    public string ReorderKey(object item) => ((NdefRecordEditorViewModel)item).Key;
    public void OpenItem(object item) { /* A record is edited in place. */ }
    public void MoveItem(int from, int to) => Records.Move(from, to);
    public void CommitOrder() => RecordsEdited();
}

/// <summary>One record being edited: the fields of its kind, turned into an <see cref="NdefRecord"/> by <see cref="Build"/>.</summary>
public sealed class NdefRecordEditorViewModel(NdefRecordKind kind, Action changed, NdefRecord? original = null) : ViewModelBase
{
    private readonly byte[] _id = original?.Id ?? [];
    private bool _modified = original is null;
    private readonly NdefRecordKind _kind = kind;
    private string _uri = "https://";
    private string _phone = string.Empty;
    private string _email = string.Empty;
    private string _subject = string.Empty;
    private string _message = string.Empty;
    private string _latitude = string.Empty;
    private string _longitude = string.Empty;
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
        // Links phones act on (call, email, SMS, map) get fields of their own.
        var isPhone = NdefUris.TryPhone(uri, out var phone);
        var isEmail = NdefUris.TryEmail(uri, out var email);
        var isSms = NdefUris.TrySms(uri, out var sms);
        var isGeo = NdefUris.TryGeo(uri, out var geo);
        var kind = isPhone ? NdefRecordKind.Phone
            : isEmail ? NdefRecordKind.Email
            : isSms ? NdefRecordKind.Sms
            : isGeo ? NdefRecordKind.Geo
            : isUri ? NdefRecordKind.Link
            : isText ? NdefRecordKind.Text
            : isWifi ? NdefRecordKind.Wifi
            : isApp ? NdefRecordKind.AndroidApp
            : r.Tnf == NdefTnf.Media && Ndef.PayloadText(r.Payload) is not null ? NdefRecordKind.Mime
            : NdefRecordKind.Raw;
        return new NdefRecordEditorViewModel(kind, changed, r)
        {
            _uri = uri,
            _phone = isPhone ? phone : isSms ? sms.Number : string.Empty,
            _email = email.Address,
            _subject = email.Subject,
            _message = isSms ? sms.Body : email.Body,
            _latitude = isGeo ? Coordinate(geo.Latitude) : string.Empty,
            _longitude = isGeo ? Coordinate(geo.Longitude) : string.Empty,
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

    public IReadOnlyList<NdefWifiAuth> WifiAuths { get; } = Enum.GetValues<NdefWifiAuth>();
    public IReadOnlyList<NdefTnf> Tnfs { get; } =
        [NdefTnf.WellKnown, NdefTnf.Media, NdefTnf.AbsoluteUri, NdefTnf.External, NdefTnf.Unknown, NdefTnf.Empty];

    /// <summary>Identity for dragging; a record keeps it for as long as it is in the list.</summary>
    public string Key { get; } = Guid.NewGuid().ToString("N");

    /// <summary>What the record is edited as; fixed once added.</summary>
    public NdefRecordKind Kind => _kind;

    public string Title => _kind.Label();

    /// <summary>The <c>Icon.*</c> key of the kind's icon.</summary>
    public string IconKey => "ndef" + _kind;

    public bool IsLink => _kind == NdefRecordKind.Link;
    public bool IsPhone => _kind == NdefRecordKind.Phone;
    public bool IsEmail => _kind == NdefRecordKind.Email;
    public bool IsSms => _kind == NdefRecordKind.Sms;
    public bool IsGeo => _kind == NdefRecordKind.Geo;

    /// <summary>The number of a phone or SMS record.</summary>
    public bool HasNumber => _kind is NdefRecordKind.Phone or NdefRecordKind.Sms;

    /// <summary>The message of an email or SMS record.</summary>
    public bool HasMessage => _kind is NdefRecordKind.Email or NdefRecordKind.Sms;
    public bool IsText => _kind == NdefRecordKind.Text;
    public bool IsWifi => _kind == NdefRecordKind.Wifi;
    public bool IsAndroidApp => _kind == NdefRecordKind.AndroidApp;
    public bool IsMime => _kind == NdefRecordKind.Mime;
    public bool IsRaw => _kind == NdefRecordKind.Raw;

    public string Uri { get => _uri; set { this.RaiseAndSetIfChanged(ref _uri, value); Edited(); } }
    public string Phone { get => _phone; set { this.RaiseAndSetIfChanged(ref _phone, value); Edited(); } }
    public string Email { get => _email; set { this.RaiseAndSetIfChanged(ref _email, value); Edited(); } }
    public string Subject { get => _subject; set { this.RaiseAndSetIfChanged(ref _subject, value); Edited(); } }
    public string Message { get => _message; set { this.RaiseAndSetIfChanged(ref _message, value); Edited(); } }
    public string Latitude { get => _latitude; set { this.RaiseAndSetIfChanged(ref _latitude, value); Edited(); } }
    public string Longitude { get => _longitude; set { this.RaiseAndSetIfChanged(ref _longitude, value); Edited(); } }
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

    /// <summary>The phone number typed: digits, with an optional leading + and spacing for reading.</summary>
    private string Number()
    {
        var number = _phone.Trim();
        if (number.Length == 0 || !number.Any(char.IsAsciiDigit)
            || number.Any(c => !char.IsAsciiDigit(c) && c is not ('+' or ' ' or '-' or '(' or ')' or '.' or '*' or '#')))
            throw new FormatException("Enter a phone number, e.g. +1 555 0100.");
        return number;
    }

    private static string Coordinate(double degrees) => degrees.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private NdefRecord Create()
    {
        switch (_kind)
        {
            case NdefRecordKind.Link:
                if (string.IsNullOrWhiteSpace(_uri) || _uri.Trim() is "https://" or "http://")
                    throw new FormatException("Enter a link.");
                return Ndef.Uri(_uri.Trim());
            case NdefRecordKind.Phone:
                return Ndef.Uri(NdefUris.Phone(Number()));
            case NdefRecordKind.Email:
                if (!_email.Contains('@') || _email.Trim().Contains(' ')) throw new FormatException("Enter an email address.");
                return Ndef.Uri(NdefUris.Email(new NdefEmail(_email.Trim(), _subject, _message)));
            case NdefRecordKind.Sms:
                return Ndef.Uri(NdefUris.Sms(new NdefSms(Number(), _message)));
            case NdefRecordKind.Geo:
                if (!NdefUris.TryCoordinate(_latitude, 90, out var lat))
                    throw new FormatException("Enter the latitude in degrees, -90 to 90, e.g. 48.8584.");
                if (!NdefUris.TryCoordinate(_longitude, 180, out var lon))
                    throw new FormatException("Enter the longitude in degrees, -180 to 180, e.g. 2.2945.");
                return Ndef.Uri(NdefUris.Geo(new NdefGeo(lat, lon)));
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
