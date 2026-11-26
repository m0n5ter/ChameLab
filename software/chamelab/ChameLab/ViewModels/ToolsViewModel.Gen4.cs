using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed record Gen4Option(int Value, string Label)
{
    public string Display => $"{Value:X2} · {Label}";
}

public sealed record Gen4MemorySetting(string Label, int Address, int Length, int Stride, bool IsPack = false);

public enum Gen4ConfirmKind
{
    None,
    Apply,
    PermanentLock,
    WriteUid,
    WriteMemory,
    WriteBlock,
}

/// <summary>
/// Gen4 Ultimate / GTU magic card configuration tool (<c>0xCF</c> backdoor protocol over
/// <see cref="Command.Hf14aRaw"/>): read/write the 30-byte configuration, permanent lock,
/// UID updates preserving manufacturer/lock bytes, memory-backed settings (OTP, NTAG password,
/// PACK, version, originality signature), and raw 16-byte block access.
/// </summary>
public sealed partial class ToolsViewModel
{
    private Gen4Configuration? _gen4Config;
    private int _gen4Protocol;
    private int _gen4UidSize;
    private int _gen4ShadowMode = 2;
    private int _gen4UlMode;
    private int _gen4Block0Mode = 2;
    private int _gen4MemoryTargetIndex;
    private int? _gen4PackPage;
    private Func<Task>? _gen4PendingAction;

    private void InitGen4(IObservable<bool> idle)
    {
        OpenGen4Command = ReactiveCommand.Create(() => { Tool = SniffTool.Gen4; }, idle);
        var canUse = this.WhenAnyValue(x => x.IsBusy, x => x.IsConnected, x => x.IsGen4Supported,
            (busy, connected, supported) => !busy && connected && supported != false);
        var canMemory = this.WhenAnyValue(x => x.IsBusy, x => x.IsConnected, x => x.IsGen4Supported, x => x.Gen4NeedsPackPage,
            (busy, connected, supported, needsPack) => !busy && connected && supported != false && !needsPack);
        Gen4ReadCommand = ReactiveCommand.CreateFromTask(ReadGen4Async, canUse);
        Gen4ApplyCommand = ReactiveCommand.Create(RequestGen4Apply, canUse);
        Gen4PermanentLockCommand = ReactiveCommand.Create(RequestGen4PermanentLock, canUse);
        Gen4WriteUidCommand = ReactiveCommand.Create(RequestGen4WriteUid, canUse);
        Gen4ReadMemoryCommand = ReactiveCommand.CreateFromTask(ReadGen4MemoryAsync, canMemory);
        Gen4WriteMemoryCommand = ReactiveCommand.Create(RequestGen4WriteMemory, canMemory);
        Gen4ReadBlockCommand = ReactiveCommand.CreateFromTask(ReadGen4BlockAsync, canUse);
        Gen4WriteBlockCommand = ReactiveCommand.Create(RequestGen4WriteBlock, canUse);
        CancelGen4ConfirmCommand = ReactiveCommand.Create(CancelGen4Confirm);
        ConfirmGen4ActionCommand = ReactiveCommand.CreateFromTask(ConfirmGen4ActionAsync, idle);
        RebuildGen4Options();
        LocalizationService.LanguageChanged += () => Dispatcher.UIThread.Post(RebuildGen4Options);
    }

    public ICommand OpenGen4Command { get; private set; } = null!;
    public ICommand Gen4ReadCommand { get; private set; } = null!;
    public ICommand Gen4ApplyCommand { get; private set; } = null!;
    public ICommand Gen4PermanentLockCommand { get; private set; } = null!;
    public ICommand Gen4WriteUidCommand { get; private set; } = null!;
    public ICommand Gen4ReadMemoryCommand { get; private set; } = null!;
    public ICommand Gen4WriteMemoryCommand { get; private set; } = null!;
    public ICommand Gen4ReadBlockCommand { get; private set; } = null!;
    public ICommand Gen4WriteBlockCommand { get; private set; } = null!;
    public ICommand CancelGen4ConfirmCommand { get; private set; } = null!;
    public ICommand ConfirmGen4ActionCommand { get; private set; } = null!;

    public bool? IsGen4Supported
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsGen4Unsupported));
        }
    }

    public bool IsGen4Unsupported => IsGen4Supported == false;
    public bool Gen4HasConfig => _gen4Config is not null;

    public string Gen4Password
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(Gen4PasswordError));
        }
    } = "00 00 00 00";

    public string? Gen4PasswordError => ValidateHex(Gen4Password, length: 4);

    public string Gen4NewPassword
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(Gen4NewPasswordError));
        }
    } = string.Empty;

    public string? Gen4NewPasswordError => Gen4HasConfig ? ValidateHex(Gen4NewPassword, length: 4) : null;

    public string Gen4Ats
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(Gen4AtsError));
        }
    } = string.Empty;

    public string? Gen4AtsError => Gen4HasConfig ? ValidateHex(Gen4Ats, maxLength: Gen4Configuration.MaxAtsLength) : null;

    public string Gen4Atqa
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(Gen4AtqaError));
        }
    } = string.Empty;

    public string? Gen4AtqaError => Gen4HasConfig ? ValidateHex(Gen4Atqa, length: 2) : null;

    public string Gen4Sak
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(Gen4SakError));
        }
    } = string.Empty;

    public string? Gen4SakError => Gen4HasConfig ? ValidateHex(Gen4Sak, length: 1) : null;

    public string Gen4MaxBlock
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(Gen4MaxBlockError));
        }
    } = string.Empty;

    public string? Gen4MaxBlockError => Gen4HasConfig ? ValidateHex(Gen4MaxBlock, length: 1) : null;

    public string Gen4Uid
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public string? Gen4UidByteLengths => _gen4Config?.UidLength is > 0 and var len ? len.ToString() : null;
    public string Gen4UidPlaceholder => _gen4Config?.UidLength switch
    {
        4 => "01 02 03 04",
        7 => "04 11 22 33 44 55 66",
        10 => "04 11 22 33 44 55 66 77 88 99",
        _ => "",
    };

    public string Gen4BlockAddress
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "00";

    public string Gen4BlockData
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public string Gen4MemoryData
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public string? Gen4Version
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(Gen4HasVersion));
        }
    }

    public bool Gen4HasVersion => !string.IsNullOrEmpty(Gen4Version);

    public string? Gen4Status
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(Gen4HasStatus));
        }
    }

    public bool Gen4HasStatus => !string.IsNullOrEmpty(Gen4Status);

    public bool Gen4Failed
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    // ---- Dropdown options ----------------------------------------------

    public IReadOnlyList<Gen4Option> Gen4ProtocolOptions { get; private set; } = [];
    public IReadOnlyList<Gen4Option> Gen4UidSizeOptions { get; private set; } = [];
    public IReadOnlyList<Gen4Option> Gen4ShadowOptions { get; private set; } = [];
    public IReadOnlyList<Gen4Option> Gen4UlModeOptions { get; private set; } = [];
    public IReadOnlyList<Gen4Option> Gen4Block0Options { get; private set; } = [];
    public IReadOnlyList<Gen4Option> Gen4MemorySettingOptions { get; private set; } = [];

    public IReadOnlyList<Gen4Option> Gen4PackPageOptions { get; } =
    [
        new(0x13, "06A0 / 6666"),
        new(0xE6, "E6"),
    ];

    public Gen4Option? Gen4ProtocolOption
    {
        get => Gen4ProtocolOptions.FirstOrDefault(o => o.Value == _gen4Protocol);
        set { if (value is not null && value.Value != _gen4Protocol) { _gen4Protocol = value.Value; this.RaisePropertyChanged(); } }
    }

    public Gen4Option? Gen4UidSizeOption
    {
        get => Gen4UidSizeOptions.FirstOrDefault(o => o.Value == _gen4UidSize);
        set { if (value is not null && value.Value != _gen4UidSize) { _gen4UidSize = value.Value; this.RaisePropertyChanged(); } }
    }

    public Gen4Option? Gen4ShadowOption
    {
        get => Gen4ShadowOptions.FirstOrDefault(o => o.Value == _gen4ShadowMode);
        set
        {
            if (value is null || value.Value == _gen4ShadowMode) return;
            _gen4ShadowMode = value.Value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(Gen4ShowShadowWarning));
        }
    }

    public bool Gen4ShowShadowWarning => _gen4ShadowMode is 1 or 4;

    public Gen4Option? Gen4UlModeOption
    {
        get => Gen4UlModeOptions.FirstOrDefault(o => o.Value == _gen4UlMode);
        set { if (value is not null && value.Value != _gen4UlMode) { _gen4UlMode = value.Value; this.RaisePropertyChanged(); } }
    }

    public Gen4Option? Gen4Block0Option
    {
        get => Gen4Block0Options.FirstOrDefault(o => o.Value == _gen4Block0Mode);
        set { if (value is not null && value.Value != _gen4Block0Mode) { _gen4Block0Mode = value.Value; this.RaisePropertyChanged(); } }
    }

    public Gen4Option? Gen4MemorySettingOption
    {
        get => _gen4MemoryTargetIndex >= 0 && _gen4MemoryTargetIndex < Gen4MemorySettingOptions.Count
            ? Gen4MemorySettingOptions[_gen4MemoryTargetIndex]
            : null;
        set
        {
            if (value is null || value.Value == _gen4MemoryTargetIndex) return;
            _gen4MemoryTargetIndex = value.Value;
            Gen4MemoryData = string.Empty;
            this.RaisePropertyChanged();
            RaiseMemoryMetadataChanged();
        }
    }

    public Gen4Option? Gen4PackPageOption
    {
        get => _gen4PackPage is { } page ? Gen4PackPageOptions.FirstOrDefault(o => o.Value == page) : null;
        set
        {
            if (value is null || value.Value == _gen4PackPage) return;
            _gen4PackPage = value.Value;
            Gen4MemoryData = string.Empty;
            this.RaisePropertyChanged();
            RaiseMemoryMetadataChanged();
        }
    }

    public bool Gen4IsPackSelected => _gen4Config?.Protocol == 1 && _gen4MemoryTargetIndex == 2;
    public bool Gen4NeedsPackPage => Gen4IsPackSelected && _gen4PackPage is null;

    public string Gen4MemoryValueHelp
    {
        get
        {
            var s = CurrentGen4MemorySetting();
            return s is null ? string.Empty : string.Format(Strings.Tools_Gen4MemoryValueHelp, s.Length, $"{s.Address:X2}");
        }
    }

    // ---- Modal confirmations -------------------------------------------

    public Gen4ConfirmKind Gen4PendingConfirm
    {
        get;
        private set
        {
            if (field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsGen4Confirming));
            this.RaisePropertyChanged(nameof(Gen4IsDangerConfirm));
            this.RaisePropertyChanged(nameof(Gen4ConfirmTitle));
            this.RaisePropertyChanged(nameof(Gen4ConfirmMessage));
            this.RaisePropertyChanged(nameof(Gen4ConfirmButtonText));
        }
    }

    public bool IsGen4Confirming => Gen4PendingConfirm != Gen4ConfirmKind.None;
    public bool Gen4IsDangerConfirm => Gen4PendingConfirm == Gen4ConfirmKind.PermanentLock;

    public string Gen4ConfirmTitle => Gen4PendingConfirm == Gen4ConfirmKind.PermanentLock
        ? Strings.Tools_Gen4PermanentLock
        : Strings.Tools_Gen4Title;

    public string Gen4ConfirmMessage => Gen4PendingConfirm switch
    {
        Gen4ConfirmKind.Apply => Strings.Tools_Gen4ApplyWarning,
        Gen4ConfirmKind.PermanentLock => Strings.Tools_Gen4FuseWarning,
        Gen4ConfirmKind.WriteUid => Strings.Tools_Gen4UidWarning,
        Gen4ConfirmKind.WriteMemory or Gen4ConfirmKind.WriteBlock => Strings.Tools_Gen4BlockWarning,
        _ => string.Empty,
    };

    public string Gen4ConfirmButtonText => Gen4PendingConfirm == Gen4ConfirmKind.PermanentLock
        ? Strings.Tools_Gen4ConfirmLock
        : Strings.Tools_Gen4ConfirmWrite;

    private void CancelGen4Confirm()
    {
        _gen4PendingAction = null;
        Gen4PendingConfirm = Gen4ConfirmKind.None;
    }

    private async Task ConfirmGen4ActionAsync()
    {
        var action = _gen4PendingAction;
        _gen4PendingAction = null;
        Gen4PendingConfirm = Gen4ConfirmKind.None;
        if (action is not null) await action();
    }

    // ---- Actions -------------------------------------------------------

    private async Task ReadGen4Async()
    {
        byte[] password;
        try { password = Gen4Client.ParseHex(Gen4Password, length: 4); }
        catch (FormatException ex) { SetGen4Error(ex.Message); return; }

        await RunGen4Async(password, async client =>
        {
            SetGen4Config(null);
            Gen4Version = null;
            _gen4PackPage = null;
            var config = await client.ReadConfigurationAsync();
            byte[]? version = null;
            // Version info is optional on older cards; a failed query must not hide 0xC6.
            try { version = await client.ReadVersionAsync(); }
            catch { /* older card */ }

            Dispatcher.UIThread.Post(() =>
            {
                SetGen4Config(config);
                if (version is not null)
                {
                    Gen4Version = Gen4Client.ToSpacedHex(version);
                    _gen4PackPage = Gen4Client.PackPage(version);
                    this.RaisePropertyChanged(nameof(Gen4PackPageOption));
                    RaiseMemoryMetadataChanged();
                }
            });
        });
    }

    private void RequestGen4Apply() => RequestGen4Apply(permanent: false);
    private void RequestGen4PermanentLock() => RequestGen4Apply(permanent: true);

    private void RequestGen4Apply(bool permanent)
    {
        if (_gen4Config is not { } current) return;
        byte[] password;
        Gen4Configuration edited;
        try
        {
            password = Gen4Client.ParseHex(Gen4Password, length: 4);
            edited = BuildEditedGen4Configuration(current);
        }
        catch (FormatException ex)
        {
            SetGen4Error(ex.Message);
            return;
        }

        _gen4PendingAction = () => RunGen4Async(password, async client =>
        {
            await client.WriteConfigurationAsync(edited, permanent);
            Dispatcher.UIThread.Post(() =>
            {
                // Keep the new credential even if the readback fails after a successful write.
                Gen4Password = Gen4Client.ToSpacedHex(edited.Password);
                SetGen4Config(null);
                Gen4Failed = false;
                Gen4Status = Strings.Tools_Gen4WrittenReadFailed;
            });
            var actual = await client.ReadConfigurationAsync();
            Dispatcher.UIThread.Post(() =>
            {
                SetGen4Config(actual);
                var matches = actual.RawBytes.SequenceEqual(edited.RawBytes);
                Gen4Failed = !matches;
                Gen4Status = matches ? Strings.Tools_Gen4Verified : Strings.Tools_Gen4VerifyFailed;
            });
        });
        Gen4PendingConfirm = permanent ? Gen4ConfirmKind.PermanentLock : Gen4ConfirmKind.Apply;
    }

    private void RequestGen4WriteUid()
    {
        if (_gen4Config is not { } config) return;
        if (config.UidLength == 0 || config.Protocol > 1)
        {
            SetGen4Error(Strings.Tools_Gen4UidConfigUnsupported);
            return;
        }
        byte[] password, uid;
        try
        {
            password = Gen4Client.ParseHex(Gen4Password, length: 4);
            uid = Gen4Client.ParseHex(Gen4Uid, length: config.UidLength);
        }
        catch (FormatException ex)
        {
            SetGen4Error(ex.Message);
            return;
        }

        _gen4PendingAction = () => RunGen4Async(password, async client =>
        {
            await client.WriteUidAsync(config, uid);
            Dispatcher.UIThread.Post(() =>
            {
                Gen4Failed = false;
                Gen4Status = Strings.Tools_Gen4UidWritten;
            });
        });
        Gen4PendingConfirm = Gen4ConfirmKind.WriteUid;
    }

    private async Task ReadGen4MemoryAsync()
    {
        if (CurrentGen4MemorySetting() is not { } setting || Gen4NeedsPackPage) return;
        byte[] password;
        try { password = Gen4Client.ParseHex(Gen4Password, length: 4); }
        catch (FormatException ex) { SetGen4Error(ex.Message); return; }

        await RunGen4Async(password, async client =>
        {
            var data = await client.ReadMemoryAsync(setting.Address, setting.Length, setting.Stride);
            Dispatcher.UIThread.Post(() => Gen4MemoryData = Gen4Client.ToSpacedHex(data));
        });
    }

    private void RequestGen4WriteMemory()
    {
        if (CurrentGen4MemorySetting() is not { } setting || Gen4NeedsPackPage) return;
        byte[] password, data;
        try
        {
            password = Gen4Client.ParseHex(Gen4Password, length: 4);
            data = Gen4Client.ParseHex(Gen4MemoryData, length: setting.Length);
        }
        catch (FormatException ex)
        {
            SetGen4Error(ex.Message);
            return;
        }

        _gen4PendingAction = () => RunGen4Async(password, async client =>
        {
            await client.WriteMemoryAsync(setting.Address, data, setting.Stride);
            var actual = await client.ReadMemoryAsync(setting.Address, setting.Length, setting.Stride);
            Dispatcher.UIThread.Post(() =>
            {
                var matches = actual.AsSpan().SequenceEqual(data);
                Gen4Failed = !matches;
                Gen4Status = matches ? Strings.Tools_Gen4Verified : Strings.Tools_Gen4VerifyFailed;
            });
        });
        Gen4PendingConfirm = Gen4ConfirmKind.WriteMemory;
    }

    private async Task ReadGen4BlockAsync()
    {
        byte[] password;
        int block;
        try
        {
            password = Gen4Client.ParseHex(Gen4Password, length: 4);
            block = Gen4Client.ParseHex(Gen4BlockAddress, length: 1)[0];
        }
        catch (FormatException ex) { SetGen4Error(ex.Message); return; }

        await RunGen4Async(password, async client =>
        {
            var data = await client.ReadBlockAsync(block);
            Dispatcher.UIThread.Post(() => Gen4BlockData = Gen4Client.ToSpacedHex(data));
        });
    }

    private void RequestGen4WriteBlock()
    {
        byte[] password, data;
        int block;
        try
        {
            password = Gen4Client.ParseHex(Gen4Password, length: 4);
            block = Gen4Client.ParseHex(Gen4BlockAddress, length: 1)[0];
            data = Gen4Client.ParseHex(Gen4BlockData, length: 16);
        }
        catch (FormatException ex)
        {
            SetGen4Error(ex.Message);
            return;
        }

        _gen4PendingAction = () => RunGen4Async(password, async client =>
        {
            await client.WriteBlockAsync(block, data);
            var actual = await client.ReadBlockAsync(block);
            Dispatcher.UIThread.Post(() =>
            {
                var matches = actual.AsSpan().SequenceEqual(data);
                Gen4Failed = !matches;
                Gen4Status = matches ? Strings.Tools_Gen4Verified : Strings.Tools_Gen4VerifyFailed;
            });
        });
        Gen4PendingConfirm = Gen4ConfirmKind.WriteBlock;
    }

    // ---- Helpers -------------------------------------------------------

    private Task RunGen4Async(byte[] password, Func<Gen4Client, Task> work) => RunAwakeAsync(async () =>
    {
        Dispatcher.UIThread.Post(() =>
        {
            Gen4Status = null;
            Gen4Failed = false;
        });
        try
        {
            var client = _device.Client ?? throw new InvalidOperationException(Strings.Common_ConnectFirst);
            if (_device.DeviceInfo?.Model == DeviceModel.Lite)
                throw new InvalidOperationException(Strings.Tools_Gen4UltraRequired);

            var gen4 = new Gen4Client(client, password);
            var mode = await client.GetDeviceModeAsync();
            try
            {
                if (mode != DeviceMode.Reader) await client.SetDeviceModeAsync(DeviceMode.Reader);
                await work(gen4);
            }
            finally
            {
                if (mode != DeviceMode.Reader)
                    try { await client.SetDeviceModeAsync(mode); } catch { /* outcome matters more */ }
            }
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => SetGen4Error(ex.Message));
        }
    });

    private void SetGen4Error(string message)
    {
        Gen4Failed = true;
        Gen4Status = message;
    }

    private void SetGen4Config(Gen4Configuration? config)
    {
        _gen4Config = config;
        if (config is not null)
        {
            _gen4MemoryTargetIndex = 0;
            Gen4MemoryData = string.Empty;
            _gen4Protocol = config.Protocol;
            _gen4UidSize = config.UidSizeCode;
            _gen4ShadowMode = config.ShadowMode;
            _gen4UlMode = config.UltralightMode;
            _gen4Block0Mode = config.Block0Mode;
            Gen4NewPassword = Gen4Client.ToSpacedHex(config.Password);
            Gen4Ats = Gen4Client.ToSpacedHex(config.Ats);
            Gen4Atqa = Gen4Client.ToSpacedHex(config.Atqa);
            Gen4Sak = Gen4Client.ToSpacedHex([config.Sak]);
            Gen4MaxBlock = Gen4Client.ToSpacedHex([config.MaxBlock]);
        }
        RebuildGen4Options();
        this.RaisePropertyChanged(nameof(Gen4HasConfig));
        this.RaisePropertyChanged(nameof(Gen4NewPasswordError));
        this.RaisePropertyChanged(nameof(Gen4AtsError));
        this.RaisePropertyChanged(nameof(Gen4AtqaError));
        this.RaisePropertyChanged(nameof(Gen4SakError));
        this.RaisePropertyChanged(nameof(Gen4MaxBlockError));
        this.RaisePropertyChanged(nameof(Gen4UidByteLengths));
        this.RaisePropertyChanged(nameof(Gen4UidPlaceholder));
        this.RaisePropertyChanged(nameof(Gen4ShowShadowWarning));
        RaiseMemoryMetadataChanged();
    }

    private Gen4Configuration BuildEditedGen4Configuration(Gen4Configuration current)
    {
        var newPwd = Gen4Client.ParseHex(Gen4NewPassword, length: 4);
        var ats = Gen4Client.ParseHex(Gen4Ats, maxLength: Gen4Configuration.MaxAtsLength);
        var atqa = Gen4Client.ParseHex(Gen4Atqa, length: 2);
        var sak = Gen4Client.ParseHex(Gen4Sak, length: 1)[0];
        var maxBlock = Gen4Client.ParseHex(Gen4MaxBlock, length: 1)[0];
        return current.WithEdits(
            (byte)_gen4Protocol,
            (byte)_gen4UidSize,
            newPwd,
            (byte)_gen4ShadowMode,
            ats,
            atqa,
            sak,
            (byte)_gen4UlMode,
            maxBlock,
            (byte)_gen4Block0Mode);
    }

    private static string? ValidateHex(string? text, int? length = null, int? maxLength = null)
    {
        try
        {
            Gen4Client.ParseHex(text, length, maxLength);
            return null;
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
    }

    private IReadOnlyList<Gen4MemorySetting> CurrentGen4MemorySettings()
    {
        var list = new List<Gen4MemorySetting>();
        if (_gen4Config?.Protocol == 1)
        {
            list.Add(new(Strings.Tools_Gen4Otp, 0x03, 4, 4));
            list.Add(new(Strings.Tools_Gen4NtagPassword, 0xE5, 4, 4));
            list.Add(new("PACK", _gen4PackPage ?? 0x13, 2, 4, IsPack: true));
            list.Add(new(Strings.Tools_Gen4Version, 0xFA, 8, 4));
            list.Add(new(Strings.Tools_Gen4Signature, 0xF2, 32, 4));
        }
        else
        {
            list.Add(new(Strings.Tools_Gen4Signature, 0x45, 32, 16));
        }
        return list;
    }

    private Gen4MemorySetting? CurrentGen4MemorySetting()
    {
        var settings = CurrentGen4MemorySettings();
        return _gen4MemoryTargetIndex >= 0 && _gen4MemoryTargetIndex < settings.Count
            ? settings[_gen4MemoryTargetIndex]
            : null;
    }

    private void RaiseMemoryMetadataChanged()
    {
        this.RaisePropertyChanged(nameof(Gen4IsPackSelected));
        this.RaisePropertyChanged(nameof(Gen4NeedsPackPage));
        this.RaisePropertyChanged(nameof(Gen4MemoryValueHelp));
    }

    private void RebuildGen4Options()
    {
        Gen4ProtocolOptions = BuildOptions(["MIFARE Classic", "Ultralight / NTAG"], _gen4Protocol);
        Gen4UidSizeOptions = BuildOptions(["4", "7", "10"], _gen4UidSize);
        Gen4ShadowOptions = BuildOptions([
            Strings.Tools_Gen4Prewrite,
            Strings.Tools_Gen4Restore,
            Strings.Tools_Gen4Disabled,
            Strings.Tools_Gen4HighSpeed,
            Strings.Tools_Gen4Split,
        ], _gen4ShadowMode);
        Gen4UlModeOptions = BuildOptions(["Ultralight EV1", "NTAG", "Ultralight C", "Ultralight"], _gen4UlMode);
        Gen4Block0Options = BuildOptions([
            Strings.Tools_Gen4Enabled,
            Strings.Tools_Gen4Disabled,
            Strings.Tools_Gen4FactoryDefault,
        ], _gen4Block0Mode);

        var memSettings = CurrentGen4MemorySettings();
        Gen4MemorySettingOptions = memSettings.Select((s, i) => new Gen4Option(i, s.Label)).ToList();

        this.RaisePropertyChanged(nameof(Gen4ProtocolOptions));
        this.RaisePropertyChanged(nameof(Gen4UidSizeOptions));
        this.RaisePropertyChanged(nameof(Gen4ShadowOptions));
        this.RaisePropertyChanged(nameof(Gen4UlModeOptions));
        this.RaisePropertyChanged(nameof(Gen4Block0Options));
        this.RaisePropertyChanged(nameof(Gen4MemorySettingOptions));
        this.RaisePropertyChanged(nameof(Gen4ProtocolOption));
        this.RaisePropertyChanged(nameof(Gen4UidSizeOption));
        this.RaisePropertyChanged(nameof(Gen4ShadowOption));
        this.RaisePropertyChanged(nameof(Gen4UlModeOption));
        this.RaisePropertyChanged(nameof(Gen4Block0Option));
        this.RaisePropertyChanged(nameof(Gen4MemorySettingOption));
        this.RaisePropertyChanged(nameof(Gen4PackPageOption));
        this.RaisePropertyChanged(nameof(Gen4MemoryValueHelp));
    }

    private static IReadOnlyList<Gen4Option> BuildOptions(IReadOnlyList<string> labels, int selectedValue)
    {
        var list = labels.Select((label, i) => new Gen4Option(i, label)).ToList();
        if (selectedValue >= 0 && list.All(o => o.Value != selectedValue))
            list.Add(new Gen4Option(selectedValue, $"0x{selectedValue:X2}"));
        return list;
    }

    private void ResetGen4OnDisconnect()
    {
        IsGen4Supported = null;
        _gen4PendingAction = null;
        Gen4PendingConfirm = Gen4ConfirmKind.None;
        _gen4PackPage = null;
        Gen4Version = null;
        Gen4Status = null;
        Gen4Failed = false;
        SetGen4Config(null);
    }
}
