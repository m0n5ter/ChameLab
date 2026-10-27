using System.Collections.Generic;
using System.Linq;

namespace ChameLab.Protocol;

public enum TagSenseType : byte
{
    Undefined = 0,
    /// <summary>125 kHz low frequency.</summary>
    Lf = 1,
    /// <summary>13.56 MHz high frequency.</summary>
    Hf = 2,
}

/// <summary>Specific emulated/scanned tag type (mirrors <c>TagSpecificType</c>).</summary>
public enum TagSpecificType : ushort
{
    Undefined = 0,

    // LF
    Em410x = 100,
    Em410x16 = 101,
    Em410x32 = 102,
    Em410x64 = 103,
    Em410xElectra = 104,
    Pac = 150,
    Viking = 170,
    Jablotron = 180,
    HidProx = 200,
    IoProx = 201,
    FarpointePyramid = 202,
    Idteck = 310,
    TagTypesLfEnd = 999,

    // HF
    MifareMini = 1000,
    Mifare1024 = 1001,
    Mifare2048 = 1002,
    Mifare4096 = 1003,
    Ntag213 = 1100,
    Ntag215 = 1101,
    Ntag216 = 1102,
    Mf0Icu1 = 1103,
    Mf0Icu2 = 1104,
    Mf0Ul11 = 1105,
    Mf0Ul21 = 1106,
    Ntag210 = 1107,
    Ntag212 = 1108,
    Hf14a4 = 3000,
    Seos = 3001,
}

public static class TagTypeExtensions
{
    public static TagSenseType SenseType(this TagSpecificType t) => t switch
    {
        TagSpecificType.Undefined => TagSenseType.Undefined,
        < TagSpecificType.TagTypesLfEnd => TagSenseType.Lf,
        _ => TagSenseType.Hf,
    };

    public static string DisplayName(this TagSpecificType t) => t switch
    {
        TagSpecificType.Undefined => "Empty",
        TagSpecificType.Em410x => "EM410X",
        TagSpecificType.Em410x16 => "EM410X / 16",
        TagSpecificType.Em410x32 => "EM410X / 32",
        TagSpecificType.Em410x64 => "EM410X / 64",
        TagSpecificType.Em410xElectra => "EM410X Electra",
        TagSpecificType.HidProx => "HID Prox",
        TagSpecificType.IoProx => "ioProx",
        TagSpecificType.FarpointePyramid => "Farpointe / Pyramid",
        TagSpecificType.Pac => "PAC / Stanley",
        TagSpecificType.Viking => "Viking",
        TagSpecificType.Jablotron => "Jablotron",
        TagSpecificType.Idteck => "IDTECK",
        TagSpecificType.MifareMini => "MIFARE Classic Mini",
        TagSpecificType.Mifare1024 => "MIFARE Classic 1K",
        TagSpecificType.Mifare2048 => "MIFARE Classic 2K",
        TagSpecificType.Mifare4096 => "MIFARE Classic 4K",
        TagSpecificType.Ntag213 => "NTAG 213",
        TagSpecificType.Ntag215 => "NTAG 215",
        TagSpecificType.Ntag216 => "NTAG 216",
        TagSpecificType.Mf0Icu1 => "MIFARE Ultralight",
        TagSpecificType.Mf0Icu2 => "MIFARE Ultralight C",
        TagSpecificType.Mf0Ul11 => "MIFARE Ultralight EV1 (640 bit)",
        TagSpecificType.Mf0Ul21 => "MIFARE Ultralight EV1 (1312 bit)",
        TagSpecificType.Ntag210 => "NTAG 210",
        TagSpecificType.Ntag212 => "NTAG 212",
        TagSpecificType.Hf14a4 => "ISO14443-4 (T=CL)",
        TagSpecificType.Seos => "SEOS",
        _ => t.ToString(),
    };

    public static IEnumerable<TagSpecificType> All() =>
        System.Enum.GetValues<TagSpecificType>()
            .Where(t => t != TagSpecificType.TagTypesLfEnd);

    public static IEnumerable<TagSpecificType> LfTypes() =>
        All().Where(t => t > TagSpecificType.Undefined && t < TagSpecificType.TagTypesLfEnd);

    public static IEnumerable<TagSpecificType> HfTypes() =>
        All().Where(t => t > TagSpecificType.TagTypesLfEnd);
}

public enum DeviceMode : byte
{
    Tag = 0,
    Reader = 1,
}

public enum MfKeyType : byte
{
    A = 0x60,
    B = 0x61,
}

public static class MfKeyTypeExtensions
{
    /// <summary>The wire value (0x60 for key A, 0x61 for key B) the firmware expects.</summary>
    public static byte ToWire(this MfKeyType keyType) => (byte)keyType;
}

/// <summary>
/// The kind of "magic" MIFARE Classic card a dump is written onto. They differ
/// in how block 0 (the UID) can be rewritten.
/// </summary>
public enum MagicGeneration
{
    /// <summary>Gen1a (UID / "magic") — magic wakeup, then raw writes with no keys.</summary>
    Gen1a,
    /// <summary>Gen2 / CUID / FUID — a normal MIFARE where block 0 is writable with the usual keys.</summary>
    Gen2,
    /// <summary>Gen3 / APDU — a normal MIFARE where block 0 is set with a backdoor command.</summary>
    Gen3,
}

public enum ButtonType : byte
{
    A = (byte)'A',
    B = (byte)'B',
}

public enum ButtonPressFunction : byte
{
    None = 0,
    NextSlot = 1,
    PrevSlot = 2,
    Clone = 3,
    Battery = 4,
    FieldGen = 5,
}

public enum AnimationMode : byte
{
    Full = 0,
    Minimal = 1,
    None = 2,
    Symmetric = 3,
}

public enum MifareClassicPrngType : byte
{
    Static = 0,
    Weak = 1,
    Hard = 2,
}

/// <summary>How an emulated MIFARE Classic / Ultralight slot answers writes.</summary>
public enum EmulatorWriteMode : byte
{
    Normal = 0,
    Denied = 1,
    Deceive = 2,
    Shadow = 3,
    ShadowRequested = 4,
}

/// <summary>Decoded <c>MF1_GET_EMULATOR_CONFIG</c> response.</summary>
public sealed record Mf1EmulatorConfig(
    bool Detection, bool Gen1a, bool Gen2, bool BlockAntiColl, EmulatorWriteMode WriteMode);

public static class EnumLabels
{
    public static string Label(this ButtonPressFunction f) => f switch
    {
        ButtonPressFunction.None => "No function",
        ButtonPressFunction.NextSlot => "Select next slot",
        ButtonPressFunction.PrevSlot => "Select previous slot",
        ButtonPressFunction.Clone => "Read then emulate UID",
        ButtonPressFunction.Battery => "Show battery level",
        ButtonPressFunction.FieldGen => "Toggle NFC field detector",
        _ => f.ToString(),
    };

    public static string Label(this AnimationMode m) => m switch
    {
        AnimationMode.Full => "Full animation",
        AnimationMode.Minimal => "Minimal animation",
        AnimationMode.None => "No animation",
        AnimationMode.Symmetric => "Symmetric animation",
        _ => m.ToString(),
    };

    public static string Label(this MifareClassicPrngType p) => p switch
    {
        MifareClassicPrngType.Static => "Static (fixed nonce)",
        MifareClassicPrngType.Weak => "Weak (predictable)",
        MifareClassicPrngType.Hard => "Hard (unpredictable)",
        _ => p.ToString(),
    };

    public static string Label(this EmulatorWriteMode m) => m switch
    {
        EmulatorWriteMode.Normal => "Normal",
        EmulatorWriteMode.Denied => "Decline (NACK writes)",
        EmulatorWriteMode.Deceive => "Deceive (ACK, discard)",
        EmulatorWriteMode.Shadow => "Shadow (RAM only)",
        EmulatorWriteMode.ShadowRequested => "Shadow (requested)",
        _ => m.ToString(),
    };

    public static string Label(this MagicGeneration g) => g switch
    {
        MagicGeneration.Gen1a => "Gen1a (UID)",
        MagicGeneration.Gen2 => "Gen2 / CUID",
        MagicGeneration.Gen3 => "Gen3 (APDU)",
        _ => g.ToString(),
    };
}
