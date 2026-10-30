using System;
using System.Collections.Generic;
using System.Linq;

namespace ChameLab.Models;

using Protocol;

public enum DeviceModel { Ultra = 0, Lite = 1 }

public sealed record DeviceInfo
{
    public required string FirmwareVersion { get; init; }
    public required string GitVersion { get; init; }
    public required DeviceModel Model { get; init; }
    public required string ChipId { get; init; }
    public required string Address { get; init; }
    public DeviceMode Mode { get; init; }

    /// <summary>The firmware takes per-card slot LED colors (ChameLab firmware, not the original one).</summary>
    public bool SupportsSlotColors { get; init; }

    public string ModelName => Model == DeviceModel.Lite ? "Chameleon Lite" : "Chameleon Ultra";
}

public sealed record BatteryInfo(int Millivolts, int Percent, BatteryCondition? Condition = null)
{
    public double Volts => Millivolts / 1000.0;

    /// <summary>“Good condition”…; null when the firmware does not report it (before v2.3.0).</summary>
    public string? ConditionText => Condition switch
    {
        BatteryCondition.Critical => "Critical condition",
        BatteryCondition.Low => "Low condition",
        BatteryCondition.Fair => "Fair condition",
        BatteryCondition.Good => "Good condition",
        BatteryCondition.Excellent => "Excellent condition",
        _ => null,
    };
}

/// <summary>Battery condition hint the firmware derives from the remaining capacity.</summary>
public enum BatteryCondition : byte
{
    Critical = 0,
    Low = 1,
    Fair = 2,
    Good = 3,
    Excellent = 4,
}

/// <summary>Per-slot summary used to render the slot grid.</summary>
public sealed class SlotInfo
{
    public required int Index { get; init; }          // 1..8
    public TagSpecificType HfType { get; set; } = TagSpecificType.Undefined;
    public TagSpecificType LfType { get; set; } = TagSpecificType.Undefined;
    public bool HfEnabled { get; set; }
    public bool LfEnabled { get; set; }
    public string HfNick { get; set; } = string.Empty;
    public string LfNick { get; set; } = string.Empty;
    /// <summary>LED color of the HF / LF card as #RRGGBB, null = the firmware's default HF/LF color.</summary>
    public string? HfColor { get; set; }
    public string? LfColor { get; set; }
    /// <summary>UIDs of the EM410x cards the LF card cycles through after its own (filled by ReadLfCyclesAsync).</summary>
    public byte[][] LfCycleIds { get; set; } = [];
    public bool IsActive { get; set; }
}

/// <summary>MIFARE Classic emulator settings of a slot; null where the firmware can't tell.</summary>
public sealed record Mf1EmulatorSettings(
    bool Gen1a, bool Gen2, bool BlockAntiColl, bool Detection, EmulatorWriteMode WriteMode,
    MifareClassicPrngType? Prng, bool? FieldOffReset);

/// <summary>Ultralight / NTAG emulator settings of a slot; null where the firmware can't tell.</summary>
public sealed record Mf0EmulatorSettings(bool UidMagic, EmulatorWriteMode WriteMode, bool? Detection);

/// <summary>The emulator settings of a slot's HF card (at most one is set, matching its type).</summary>
public sealed record SlotEmulatorSettings(Mf1EmulatorSettings? Mf1, Mf0EmulatorSettings? Mf0)
{
    public static readonly SlotEmulatorSettings None = new(null, null);
}

/// <summary>One frequency of a slot, read back so it can be put into another slot.</summary>
public sealed record SlotSide(CardSave Card, string Nick, string? Color, bool Enabled);

/// <summary>Everything a slot holds: its HF and LF cards (null when empty) and the HF emulator settings.</summary>
public sealed record SlotContent(int Index, SlotSide? Hf, SlotSide? Lf, SlotEmulatorSettings Emulator, LfCardCycle? LfCycle = null)
{
    public IEnumerable<CardSave> Cards => new[] { Hf?.Card, Lf?.Card }.OfType<CardSave>();
}

/// <summary>Result of an ISO14443-A scan.</summary>
public sealed record Hf14aTag(byte[] Uid, byte[] Atqa, byte Sak, byte[] Ats)
{
    public string UidHex => Convert.ToHexString(Uid);
    public string AtqaHex => Convert.ToHexString(Atqa);
    public string SakHex => Sak.ToString("X2");
    public string AtsHex => Ats.Length == 0 ? "-" : Convert.ToHexString(Ats);
}

public sealed record Em410xTag(TagSpecificType Type, byte[] Id)
{
    public string IdHex => Convert.ToHexString(Id);
}

/// <summary>One captured MIFARE Classic auth nonce record (for mfkey32 recovery).</summary>
public sealed record DetectionLog(
    int Block, MfKeyType KeyType, bool IsNested, byte[] Uid, byte[] Nt, byte[] Nr, byte[] Ar)
{
    public string UidHex => Convert.ToHexString(Uid);
    public string NtHex => Convert.ToHexString(Nt);
    public string NrHex => Convert.ToHexString(Nr);
    public string ArHex => Convert.ToHexString(Ar);
    public char KeyChar => KeyType == MfKeyType.A ? 'A' : 'B';
}

/// <summary>PRNG distance between the known-key nonce and the target (device <c>Mf1DetectNtDist</c>), used by the nested attack.</summary>
public sealed record NtDistance(uint Uid, uint Distance);

/// <summary>One nested-attack nonce sample: the tag nonce, its encrypted form and (weak PRNG only) three low parity bits.</summary>
public sealed record NestedNonce(uint Nt, uint NtEnc, byte Parity);

/// <summary>One darkside round captured from the card (device <c>Mf1DarksideAcquire</c>), fed to <see cref="Services.DarksideRecovery"/>.</summary>
public sealed record DarksideItem(uint Nt, ulong Ks1, ulong Par, uint Nr, uint Ar);

/// <summary>How a darkside acquisition ended; only <see cref="Vulnerable"/> yields usable data.</summary>
public enum DarksideResult
{
    Vulnerable = 0,
    CantFixNt = 1,
    LuckAuthOk = 2,
    NotSendingNack = 3,
    TagChanged = 4,
    Fixed = 5,
}

/// <summary>Result of a non-EM LF scan: the raw emulator ID (decoded by <see cref="LfIdDecoder"/>).</summary>
public sealed record LfScanResult(TagSpecificType Type, byte[] Id)
{
    public string IdHex => Convert.ToHexString(Id);
}

/// <summary>
/// An Ultralight / NTAG card read from the reader. Unread pages are empty in
/// <see cref="CardSave.Data"/>; from <see cref="LockedFrom"/> on the card refused to answer
/// (password-protected, or the Ultralight C key pages). <see cref="KeyAccepted"/> is null when
/// no key was given; <see cref="Pack"/> is what the card answered to an accepted PWD.
/// </summary>
public sealed record UltralightDumpResult(
    CardSave Card, int PagesRead, int PageCount, int? LockedFrom, bool? KeyAccepted, byte[]? Pack);
