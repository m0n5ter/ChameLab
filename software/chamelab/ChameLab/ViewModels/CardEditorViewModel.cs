using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Input;
using ChameLab.Controls;
using ChameLab.Models;
using ChameLab.Protocol;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>
/// Editable working copy of a card: name, color, identity and memory. Used
/// both for a card being added and for a saved card; <see cref="IsDirty"/>
/// tells whether anything differs from what the editor was opened with.
/// </summary>
public sealed class CardEditorViewModel : ViewModelBase
{
    private CardSave _source;
    private string _baseline = string.Empty;
    private string _name;
    private string _color;
    private TagSpecificType _tag;
    private string _uid;
    private string _sak;
    private string _atqa;
    private string _ats;
    private List<byte[]> _data;
    private bool _isDirty;
    private HexBuffer? _memory;
    private string? _memoryWarning;
    private bool _generatedMemory;
    // Memory of another layout set aside by a type change; restored when switching back.
    private (TagSpecificType Tag, List<byte[]> Data)? _stash;

    /// <param name="restrictType">
    /// A saved card: offer only the tag types its data stays valid for (see
    /// <see cref="CardTags.CompatibleTypes"/>); a card without a type still gets them all.
    /// </param>
    /// <param name="sameFrequency">
    /// A scanned tag: offer only types of its frequency (no MIFARE for an LF scan, no EM410x for HF).
    /// </param>
    public CardEditorViewModel(CardSave source, bool restrictType = false, bool sameFrequency = false)
    {
        _source = source;
        IsTypeRestricted = restrictType && source.Tag != TagSpecificType.Undefined;
        TagChoices = IsTypeRestricted ? source.Tag.CompatibleTypes()
            : sameFrequency && source.Tag != TagSpecificType.Undefined
                ? AllTagTypes.Where(t => t.SenseType() == source.Tag.SenseType()).ToArray()
                : AllTagTypes;
        _name = source.Name;
        _color = source.Color;
        _tag = source.Tag == TagSpecificType.Undefined ? TagSpecificType.Mifare1024 : source.Tag;
        _uid = Hex.Spaced(source.Uid);
        _sak = source.Sak.ToString("X2");
        _atqa = Hex.Spaced(SwapAtqa(source.Atqa));
        _ats = Hex.Spaced(source.Ats);
        _data = source.Data.Select(d => (byte[])d.Clone()).ToList();
        RebuildRows();
        _baseline = Fingerprint();
        ClearSectorCommand = ReactiveCommand.Create(() => SelectedMemory?.ClearRows(0, SelectedMemory.RowCount));
    }

    public string Id => _source.Id;

    private static readonly IReadOnlyList<TagSpecificType> AllTagTypes =
        TagTypeExtensions.All().Where(t => t != TagSpecificType.Undefined).ToArray();

    /// <summary>Tag types offered in the type picker.</summary>
    public IReadOnlyList<TagSpecificType> TagChoices { get; }

    /// <summary>The picker is narrowed to types compatible with the saved card.</summary>
    public bool IsTypeRestricted { get; }

    public bool CanChangeTag => TagChoices.Count > 1;

    /// <summary>Preset accents (Flutter app color picker defaults).</summary>
    public static IReadOnlyList<string> Palette => SlotColors.Palette;

    public string Name { get => _name; set { this.RaiseAndSetIfChanged(ref _name, value); Touch(); } }
    public string Color
    {
        get => _color;
        set
        {
            // The swatch list deselects (and writes null) when the card's color isn't in the palette.
            if (string.IsNullOrWhiteSpace(value)) return;
            this.RaiseAndSetIfChanged(ref _color, value);
            Touch();
        }
    }

    public TagSpecificType Tag
    {
        get => _tag;
        set
        {
            if (value == _tag) return;
            var old = _tag;
            AdaptIdentity(old, value);
            if (old.IsMifareClassic() && value.IsMifareClassic()) ResizeMifareClassic(value.MifareClassicBlocks());
            else AdaptMemory(old, value);
            SetTag(value);
        }
    }

    private void SetTag(TagSpecificType value)
    {
        // Named explicitly: inside SetTag the caller name would announce "SetTag" instead of Tag.
        this.RaiseAndSetIfChanged(ref _tag, value, nameof(Tag));
        this.RaisePropertyChanged(nameof(IsHf));
        this.RaisePropertyChanged(nameof(UidLabel));
        this.RaisePropertyChanged(nameof(UidHint));
        this.RaisePropertyChanged(nameof(UidLengths));
        this.RaisePropertyChanged(nameof(IsMifareClassic));
        this.RaisePropertyChanged(nameof(IsUltralight));
        this.RaisePropertyChanged(nameof(HasUltralightConfig));
        this.RaisePropertyChanged(nameof(UltralightKeyLegend));
        this.RaisePropertyChanged(nameof(LfFields));
        this.RaisePropertyChanged(nameof(HasLfFields));
        this.RaisePropertyChanged(nameof(IsPyramid));
        RaisePyramidFields();
        RebuildRows();
        Touch();
    }

    /// <summary>
    /// Switches SAK / ATQA to the new type's usual values when they are still the old
    /// type's (or ATQA was never set), so e.g. 1K → 4K also turns 08 / 00 04 into 18 / 00 02.
    /// </summary>
    private void AdaptIdentity(TagSpecificType from, TagSpecificType to)
    {
        var uid7 = _uid.Count(Uri.IsHexDigit) > 8;
        if (to.DefaultSakAtqa(uid7) is not { } next) return;
        var untouched = !Hex.TryParse(_atqa, out var atqa) || atqa.Length == 0
                                                           || (from.DefaultSakAtqa(uid7) is { } prev && Hex.TryParse(_sak, out var sak)
                                                                                                     && sak.SequenceEqual(new[] { prev.Sak }) && atqa.SequenceEqual(SwapAtqa(prev.Atqa)));
        if (!untouched) return;
        Sak = next.Sak.ToString("X2");
        Atqa = Hex.Spaced(SwapAtqa(next.Atqa));
    }

    /// <summary>
    /// Fits the dump to a MIFARE Classic size: a bigger card gets blank sectors, a smaller one
    /// sheds trailing sectors that hold nothing (blank or unread). Sectors with data stay until
    /// the card is saved, so switching back loses nothing; <see cref="MemoryWarning"/> says so.
    /// </summary>
    private void ResizeMifareClassic(int blocks)
    {
        if (_data.Count == 0) return;
        for (var b = _data.Count; b < blocks; b++) _data.Add(MifareClassic.BlankBlock(b));
        while (_data.Count > blocks)
        {
            var first = MifareClassic.SectorFirstBlock(MifareClassic.SectorOfBlock(_data.Count - 1));
            if (first < blocks) break;
            var empty = Enumerable.Range(first, _data.Count - first)
                .All(b => _data[b].Length == 0 || MifareClassic.IsBlankBlock(b, _data[b]));
            if (!empty) break;
            _data.RemoveRange(first, _data.Count - first);
        }
    }

    /// <summary>
    /// Memory laid out for another type can't carry over: it is set aside (and comes back if
    /// the type is switched back before saving) and a blank card of the new type takes its
    /// place. A card with no memory stays identity only.
    /// </summary>
    private void AdaptMemory(TagSpecificType from, TagSpecificType to)
    {
        List<byte[]> next;
        if (_stash is { } stashed && SameLayout(stashed.Tag, to))
        {
            next = stashed.Data;
            _stash = null;
        }
        else if (_data.Count > 0)
        {
            var (uid, sak, atqa) = IdentityBytes();
            next = BlankMemory.Create(to, uid, sak, atqa);
        }
        else return;

        if (_data.Any(d => d.Length > 0)) _stash = (from, _data);
        _data = next;
        if (to.IsMifareClassic()) ResizeMifareClassic(to.MifareClassicBlocks());
    }

    /// <summary>Memory of one type is valid for the other (all MIFARE Classic sizes share a layout).</summary>
    private static bool SameLayout(TagSpecificType a, TagSpecificType b) =>
        a == b || (a.IsMifareClassic() && b.IsMifareClassic());

    /// <summary>UID, SAK and ATQA as typed so far; unparsable fields count as empty.</summary>
    private (byte[] Uid, byte Sak, byte[] Atqa) IdentityBytes() => (
        Hex.TryParse(_uid, out var u) ? u : Array.Empty<byte>(),
        Hex.TryParse(_sak, out var s) && s.Length == 1 ? s[0] : (byte)0,
        Hex.TryParse(_atqa, out var a) ? SwapAtqa(a) : Array.Empty<byte>());

    /// <summary>Blocks past the end of the chosen MIFARE Classic size (dropped on save).</summary>
    private int OverflowBlocks =>
        _tag.IsMifareClassic() ? Math.Max(0, _data.Count - _tag.MifareClassicBlocks()) : 0;

    public bool IsHf => _tag.SenseType() == TagSenseType.Hf;
    public bool IsMifareClassic => _tag.IsMifareClassic();
    public string UidLabel => IsHf ? "UID" : "Card ID";

    public string UidHint
    {
        get
        {
            // The allowed lengths are shown by the field's byte counter.
            if (IsHf) return "e.g. 04 A1 B2 C3";
            var len = _tag.LfIdLength();
            return len > 0 ? $"{len} bytes (hex)" : "hex";
        }
    }

    /// <summary>Valid UID / ID lengths in bytes for the hex field ("4,7,10"); empty when any length goes.</summary>
    public string UidLengths => IsHf ? "4,7,10" : _tag.LfIdLength() is > 0 and var len ? len.ToString() : string.Empty;

    public string Uid
    {
        get => _uid;
        set
        {
            this.RaiseAndSetIfChanged(ref _uid, value);
            this.RaisePropertyChanged(nameof(LfFields));
            this.RaisePropertyChanged(nameof(HasLfFields));
            RaisePyramidFields();
            Touch();
        }
    }

    /// <summary>Farpointe / Pyramid: the ID can be built from a facility code and card number.</summary>
    public bool IsPyramid => _tag == TagSpecificType.FarpointePyramid;

    /// <summary>Facility code of a 26-bit Pyramid ID; typing it rebuilds the ID.</summary>
    public string PyramidFacility
    {
        get => Pyramid26()?.Facility.ToString() ?? string.Empty;
        set
        {
            if (byte.TryParse(value, out var fc)) Uid = Hex.Spaced(PyramidFrame.Encode26(fc, Pyramid26()?.Card ?? 0));
        }
    }

    /// <summary>Card number of a 26-bit Pyramid ID; typing it rebuilds the ID.</summary>
    public string PyramidCard
    {
        get => Pyramid26()?.Card.ToString() ?? string.Empty;
        set
        {
            if (ushort.TryParse(value, out var cn)) Uid = Hex.Spaced(PyramidFrame.Encode26(Pyramid26()?.Facility ?? 0, cn));
        }
    }

    private (byte Facility, ushort Card)? Pyramid26()
    {
        if (!IsPyramid || !Hex.TryParse(_uid, out var id) || id.Length != PyramidFrame.Length) return null;
        var (format, fc, cn) = PyramidFrame.Decode(id);
        return format == 26 && fc is { } facility ? ((byte)facility, (ushort)cn) : null;
    }

    private void RaisePyramidFields()
    {
        this.RaisePropertyChanged(nameof(PyramidFacility));
        this.RaisePropertyChanged(nameof(PyramidCard));
    }

    /// <summary>What an LF ID encodes (card number, facility code…); empty for HF or an incomplete ID.</summary>
    public IReadOnlyList<ScanResultRow> LfFields =>
        !IsHf && Hex.TryParse(_uid, out var id)
            ? LfIdDecoder.Decode(_tag, id).Select(f => new ScanResultRow(f.Field, f.Value)).ToList()
            : Array.Empty<ScanResultRow>();

    public bool HasLfFields => LfFields.Count > 0;
    public string Sak { get => _sak; set { this.RaiseAndSetIfChanged(ref _sak, value); Touch(); } }
    public string Atqa { get => _atqa; set { this.RaiseAndSetIfChanged(ref _atqa, value); Touch(); } }
    public string Ats { get => _ats; set { this.RaiseAndSetIfChanged(ref _ats, value); Touch(); } }

    /// <summary>Something differs from the card the editor was opened with.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => this.RaiseAndSetIfChanged(ref _isDirty, value);
    }

    // ---- Memory --------------------------------------------------------

    /// <summary>The memory as edited by the hex grid; rebuilt when the rows are replaced or the tag type changes.</summary>
    public HexBuffer? Memory
    {
        get => _memory;
        private set => this.RaiseAndSetIfChanged(ref _memory, value);
    }

    private int _selectedSector;
    public int SelectedSector
    {
        get => _selectedSector;
        set
        {
            var count = _tag.IsMifareClassic() ? MifareClassic.SectorOfBlock(_tag.MifareClassicBlocks() - 1) + 1 : 0;
            var selected = count > 0 ? Math.Clamp(value, 0, count - 1) : 0;
            if (_selectedSector == selected) return;
            this.RaiseAndSetIfChanged(ref _selectedSector, selected);
            UpdateSelectedMemory();
        }
    }

    private HexBuffer? _selectedMemory;
    public HexBuffer? SelectedMemory => _selectedMemory;
    public ICommand ClearSectorCommand { get; }

    private void UpdateSelectedMemory()
    {
        var memory = Memory;
        if (_tag.IsMifareClassic() && memory is not null)
        {
            var count = MifareClassic.SectorOfBlock(_tag.MifareClassicBlocks() - 1) + 1;
            this.RaiseAndSetIfChanged(ref _selectedSector, Math.Clamp(_selectedSector, 0, count - 1), nameof(SelectedSector));
            memory = memory.Slice(MifareClassic.SectorFirstBlock(_selectedSector), MifareClassic.BlocksInSector(_selectedSector));
        }
        this.RaiseAndSetIfChanged(ref _selectedMemory, memory, nameof(SelectedMemory));
    }

    /// <summary>Problems in the memory that would break the card (e.g. corrupt access bits).</summary>
    public string? MemoryWarning
    {
        get => _memoryWarning;
        private set => this.RaiseAndSetIfChanged(ref _memoryWarning, value);
    }

    public bool HasData => _data.Any(d => d.Length > 0);

    /// <summary>Generated defaults are editable memory, but are not a physical read result.</summary>
    public CardSave BuildForReading()
    {
        var card = Build();
        if (_generatedMemory && Memory is { } memory)
            card.Data = card.Data.Select((row, index) =>
                Enumerable.Range(0, row.Length).Any(col => memory.IsChanged((long)index * memory.RowLength + col))
                    ? row : Array.Empty<byte>()).ToList();
        return card;
    }

    public string DataSummary
    {
        get
        {
            if (_generatedMemory) return "Blank memory";
            var known = _data.Count(d => d.Length > 0);
            if (_tag.IsMifareClassic())
            {
                // A sector counts once every one of its blocks was read (or entered).
                var blocks = _tag.MifareClassicBlocks();
                var sectors = MifareClassic.SectorOfBlock(blocks - 1) + 1;
                var full = Enumerable.Range(0, sectors).Count(s =>
                    Enumerable.Range(MifareClassic.SectorFirstBlock(s), MifareClassic.BlocksInSector(s))
                        .All(b => b < _data.Count && _data[b].Length > 0));
                return $"{full}/{sectors} sectors";
            }
            if (_tag.IsUltralight()) return $"{known}/{_tag.UltralightPages()} pages";
            return known > 0 ? $"{known} data rows" : "Identity only";
        }
    }

    /// <summary>Bytes per row: 16 for MIFARE Classic blocks, 4 for Ultralight pages.</summary>
    private int RowLength =>
        _data.FirstOrDefault(d => d.Length > 0)?.Length ?? (_tag.IsUltralight() ? 4 : 16);

    /// <summary>Forgets the memory, and any a type change set aside: a dump of another type no longer applies.</summary>
    public void ClearMemory()
    {
        _stash = null;
        SetData(new List<byte[]>());
    }

    private void SetData(List<byte[]> rows)
    {
        _data = rows;
        _generatedMemory = false;
        RebuildRows();
        Touch();
    }

    private void RebuildRows()
    {
        if (_memory is not null) _memory.Changed -= OnMemoryChanged;
        var mfc = _tag.IsMifareClassic();
        var ul = _tag.IsUltralight();
        var tag = _tag;
        if (!_data.Any(row => row.Length > 0) && BlankMemory.Supports(tag))
        {
            var (uid, sak, atqa) = IdentityBytes();
            _data = BlankMemory.Create(tag, uid, sak, atqa);
            _generatedMemory = true;
        }
        // A partial read retains every unread row; defaults only initialize a card with no memory.
        if (mfc)
            while (_data.Count < tag.MifareClassicBlocks()) _data.Add(Array.Empty<byte>());
        Memory = _data.Count == 0 ? null : new HexBuffer(_data, RowLength)
        {
            // MIFARE Classic sectors are set apart, each under a "Sector N · blocks a–b" heading,
            // so the gutter keeps just the block number, like the page number of other tags.
            RowLabel = row => $"{row:D3}",
            StartsGroup = row => mfc && MifareClassic.SectorFirstBlock(MifareClassic.SectorOfBlock(row)) == row,
            GroupHeader = row =>
            {
                if (!mfc || MifareClassic.SectorFirstBlock(MifareClassic.SectorOfBlock(row)) != row) return null;
                var sector = MifareClassic.SectorOfBlock(row);
                return ($"Sector {sector}", $"blocks {row}–{row + MifareClassic.BlocksInSector(sector) - 1}");
            },
            Classify = (row, col) => mfc ? MifareCellKind(row, col) : ul ? UltralightCellKind(tag, row, col) : HexCellKind.Data,
            Describe = (row, col) => mfc ? DescribeMifare(row, col) : ul ? DescribeUltralight(tag, row, col) : null,
        };
        if (_memory is not null) _memory.Changed += OnMemoryChanged;
        UpdateSelectedMemory();
        RefreshMemoryState();
    }

    private void OnMemoryChanged(object? sender, EventArgs e)
    {
        RefreshMemoryState();
        Touch();
    }

    private void RefreshMemoryState()
    {
        this.RaisePropertyChanged(nameof(HasData));
        this.RaisePropertyChanged(nameof(DataSummary));

        var warnings = new List<string>();
        if (_tag.IsMifareClassic())
        {
            var blocks = _tag.MifareClassicBlocks();
            var bad = Enumerable.Range(0, Math.Min(_data.Count, blocks))
                .Where(b => MifareClassic.IsTrailer(b) && _data[b].Length == 16 && !MifareClassic.AccessBitsValid(_data[b]))
                .Select(MifareClassic.SectorOfBlock).ToArray();
            if (bad.Length > 0)
                warnings.Add($"Invalid access bits in sector {string.Join(", ", bad)}: writing this to a card would lock the sector.");
            if (OverflowBlocks > 0)
                warnings.Add($"Sectors {MifareClassic.SectorOfBlock(blocks)}–{MifareClassic.SectorOfBlock(_data.Count - 1)} "
                    + $"don't fit a {_tag.DisplayName()} and will be dropped when the card is saved.");
        }
        if (_stash is { } stashed)
            warnings.Add($"The {stashed.Tag.DisplayName()} memory is set aside: switch back to restore it, saving drops it.");
        MemoryWarning = warnings.Count == 0 ? null : string.Join(" ", warnings);
    }

    private static HexCellKind MifareCellKind(int block, int col)
    {
        if (block == 0) return HexCellKind.Manufacturer;
        if (!MifareClassic.IsTrailer(block)) return HexCellKind.Data;
        return col switch { < 6 => HexCellKind.KeyA, < 10 => HexCellKind.AccessBits, _ => HexCellKind.KeyB };
    }

    private string DescribeMifare(int block, int col)
    {
        var sector = $"Sector {MifareClassic.SectorOfBlock(block)}";
        if (block == 0)
        {
            var uid4 = _uid.Count(Uri.IsHexDigit) <= 8;
            var what = uid4
                ? col switch { < 4 => "UID", 4 => "BCC", 5 => "SAK", 6 or 7 => "ATQA", _ => "Manufacturer data" }
                : col < 7 ? "UID" : "Manufacturer data";
            return $"{sector} · {what}";
        }
        if (!MifareClassic.IsTrailer(block)) return sector;
        var part = col switch { < 6 => "Key A", < 9 => "Access bits", 9 => "User byte", _ => "Key B" };
        if (col is >= 6 and < 9 && _data[block].Length == 16)
            part += MifareClassic.AccessBitsValid(_data[block]) ? " (valid)" : " (INVALID)";
        return $"{sector} trailer · {part}";
    }

    // Ultralight colors reuse the MIFARE Classic ones: UID = manufacturer, lock bytes = access
    // bits, configuration = key A, PWD / PACK (Ultralight C: 3DES key) = key B.
    private static HexCellKind UltralightCellKind(TagSpecificType tag, int page, int col)
    {
        var cfg0 = tag.UltralightCfg0Page();
        if (page < 2 || (page == 2 && col < 2)) return HexCellKind.Manufacturer;
        if (page == 2 || (page == tag.UltralightDynamicLockPage() && col < (tag == TagSpecificType.Mf0Icu2 ? 2 : 3)))
            return HexCellKind.AccessBits;
        if (cfg0 > 0 && page >= cfg0)
            return page < cfg0 + 2 ? HexCellKind.KeyA : page == cfg0 + 2 || col < 2 ? HexCellKind.KeyB : HexCellKind.Data;
        if (tag == TagSpecificType.Mf0Icu2 && page >= 0x2A)
            return page < CardTags.UlcFirstKeyPage ? HexCellKind.KeyA : HexCellKind.KeyB;
        return HexCellKind.Data;
    }

    private static string? DescribeUltralight(TagSpecificType tag, int page, int col)
    {
        switch (page)
        {
            case 0: return col < 3 ? "UID" : "BCC0";
            case 1: return "UID";
            case 2: return col switch { 0 => "BCC1", 1 => "Internal", _ => "Static lock bytes" };
            case 3: return tag.UltralightCfg0Page() > 0 && tag != TagSpecificType.Mf0Ul11 && tag != TagSpecificType.Mf0Ul21
                ? "Capability container" : "OTP";
        }
        if (page == tag.UltralightDynamicLockPage())
            return tag == TagSpecificType.Mf0Icu2 ? (col < 2 ? "Dynamic lock bytes" : "RFU")
                : col < 3 ? "Dynamic lock bytes" : "RFUI";
        var cfg0 = tag.UltralightCfg0Page();
        if (cfg0 > 0 && page >= cfg0)
        {
            var ntag = tag is not (TagSpecificType.Mf0Ul11 or TagSpecificType.Mf0Ul21);
            return (page - cfg0, col) switch
            {
                (0, 0) => ntag ? "CFG0 · MIRROR" : "CFG0 · MOD",
                (0, 1) => "CFG0 · RFUI",
                (0, 2) => ntag ? "CFG0 · MIRROR_PAGE" : "CFG0 · RFUI",
                (0, 3) => "CFG0 · AUTH0 (first protected page)",
                (1, 0) => "CFG1 · ACCESS (PROT, CFGLCK, AUTHLIM…)",
                (1, 1) => "CFG1 · RFUI",
                (1, 2) => "CFG1 · RFUI",
                (1, 3) => "CFG1 · RFUI",
                (2, _) => "PWD (reads back as 00)",
                (3, < 2) => "PACK (reads back as 00)",
                _ => "RFUI",
            };
        }
        if (tag == TagSpecificType.Mf0Icu2)
            return page switch
            {
                0x29 => "16-bit counter",
                0x2A => "AUTH0 (first protected page)",
                0x2B => "AUTH1 (0 = write protect only)",
                >= CardTags.UlcFirstKeyPage and < CardTags.UlcFirstKeyPage + 4 => "3DES key (not readable)",
                _ => null,
            };
        return null;
    }

    /// <summary>Ultralight / NTAG memory: drives the page legend of the memory editor.</summary>
    public bool IsUltralight => _tag.IsUltralight();

    /// <summary>The Ultralight type has configuration pages (EV1 / NTAG21x / Ultralight C).</summary>
    public bool HasUltralightConfig => _tag.UltralightCfg0Page() > 0 || _tag == TagSpecificType.Mf0Icu2;

    /// <summary>Legend of the key-colored pages: the password or, on Ultralight C, the 3DES key.</summary>
    public string UltralightKeyLegend => _tag == TagSpecificType.Mf0Icu2 ? "■ 3DES key" : "■ PWD/PACK";

    /// <summary>
    /// Replaces identity and memory with freshly read data (e.g. a dump of the
    /// scanned card) while keeping the name and color the user picked.
    /// </summary>
    public void ReplaceContent(CardSave card)
    {
        var keepName = Name;
        var keepColor = Color;
        card.Id = _source.Id;
        card.Folder = _source.Folder;
        _source = card;
        _uid = Hex.Spaced(card.Uid);
        _sak = card.Sak.ToString("X2");
        _atqa = Hex.Spaced(SwapAtqa(card.Atqa));
        _ats = Hex.Spaced(card.Ats);
        this.RaisePropertyChanged(nameof(Uid));
        RaisePyramidFields();
        this.RaisePropertyChanged(nameof(Sak));
        this.RaisePropertyChanged(nameof(Atqa));
        this.RaisePropertyChanged(nameof(Ats));
        // The new identity and memory come as read: no SAK / ATQA or size adapting.
        SetTag(card.Tag == TagSpecificType.Undefined ? TagSpecificType.Mifare1024 : card.Tag);
        Name = keepName;
        Color = keepColor;
        _stash = null;
        SetData(card.Data.Select(d => (byte[])d.Clone()).ToList());
    }

    // ---- Dirty tracking ------------------------------------------------

    private void Touch()
    {
        // Called from setters before the constructor sets the baseline.
        if (_baseline.Length == 0) return;
        IsDirty = Fingerprint() != _baseline;
    }

    private string Fingerprint()
    {
        static string N(string? s) => new string((s ?? string.Empty)
            .Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-').ToArray()).ToUpperInvariant();

        var sb = new StringBuilder();
        sb.Append(_name).Append('|').Append(CardSave.NormalizeColor(_color)).Append('|').Append((int)_tag)
          .Append('|').Append(N(_uid));
        if (IsHf) sb.Append('|').Append(N(_sak)).Append('|').Append(N(_atqa)).Append('|').Append(N(_ats));
        foreach (var row in _data) sb.Append('|').Append(Convert.ToHexString(row));
        return sb.ToString();
    }

    // ---- Build ---------------------------------------------------------

    /// <summary>Validates the form and returns the updated card; throws with a user-facing message.</summary>
    public CardSave Build()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new FormatException("Please enter a name.");

        var uid = ParseHex(Uid, UidLabel);
        byte sak = 0;
        byte[] atqa = Array.Empty<byte>(), ats = Array.Empty<byte>();
        if (IsHf)
        {
            if (uid.Length is not (4 or 7 or 10)) throw new FormatException("UID must be 4, 7 or 10 bytes long.");
            var sakBytes = ParseHex(Sak, "SAK");
            if (sakBytes.Length != 1) throw new FormatException("SAK must be 1 byte long.");
            sak = sakBytes[0];
            atqa = ParseHex(Atqa, "ATQA");
            if (atqa.Length != 2) throw new FormatException("ATQA must be 2 bytes long.");
            atqa = SwapAtqa(atqa); // field is big-endian (00 04); store device order (04 00)
            ats = ParseHex(Ats, "ATS");
        }
        else
        {
            var len = _tag.LfIdLength();
            if (len > 0 && uid.Length != len)
                throw new FormatException($"{_tag.DisplayName()} ID must be {len} bytes long.");
        }

        var card = _source.Clone();
        card.Id = _source.Id;
        card.Name = Name.Trim();
        card.Color = CardSave.NormalizeColor(Color);
        card.Tag = _tag;
        card.Uid = uid;
        card.Sak = sak;
        card.Atqa = atqa;
        card.Ats = ats;
        card.Data = _data.Take(_data.Count - OverflowBlocks).Select(d => (byte[])d.Clone()).ToList();

        if (_generatedMemory && Memory is { } memory)
        {
            var defaults = BlankMemory.Create(_tag, uid, sak, atqa);
            for (var row = 0; row < Math.Min(card.Data.Count, defaults.Count); row++)
                if (card.Data[row].Length > 0 && !Enumerable.Range(0, card.Data[row].Length)
                        .Any(col => memory.IsChanged((long)row * memory.RowLength + col)))
                    card.Data[row] = defaults[row];
        }

        // Keep block 0 in step with an edited identity so dumps stay consistent.
        var identityChanged = !uid.SequenceEqual(_source.Uid) || sak != _source.Sak || !atqa.SequenceEqual(_source.Atqa);
        if (identityChanged && _tag.IsMifareClassic() && card.Data.Count > 0 && card.Data[0].Length == 16 && uid.Length == 4)
        {
            var b0 = (byte[])card.Data[0].Clone();
            uid.CopyTo(b0, 0);
            b0[4] = (byte)(uid[0] ^ uid[1] ^ uid[2] ^ uid[3]);
            b0[5] = sak;
            atqa.CopyTo(b0, 6);
            card.Data[0] = b0;
        }
        return card;
    }

    private static byte[] ParseHex(string text, string field) =>
        Hex.TryParse(text, out var b) ? b : throw new FormatException($"{field} must be valid hex.");

    /// <summary>
    /// Converts ATQA between the high-byte-first form shown and typed in this editor (00 04,
    /// like ChameleonUltraGUI) and the low-byte-first order the card is stored and sent in (04 00).
    /// </summary>
    private static byte[] SwapAtqa(byte[] atqa) => CardTags.SwapAtqa(atqa);
}
