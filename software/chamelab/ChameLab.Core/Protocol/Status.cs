using ChameLab.Localization;
namespace ChameLab.Protocol;

/// <summary>Device response status codes (mirrors <c>chameleon_enum.Status</c>).</summary>
public enum Status : ushort
{
    HfTagOk = 0x00,
    HfTagNo = 0x01,
    HfErrStat = 0x02,
    HfErrCrc = 0x03,
    HfCollision = 0x04,
    HfErrBcc = 0x05,
    MfErrAuth = 0x06,
    HfErrParity = 0x07,
    HfErrAts = 0x08,

    LfTagOk = 0x40,
    LfTagNoFound = 0x41,

    ParErr = 0x60,
    DeviceModeError = 0x66,
    InvalidCmd = 0x67,
    Success = 0x68,
    NotImplemented = 0x69,
    FlashWriteFail = 0x70,
    FlashReadFail = 0x71,
    InvalidSlotType = 0x72,
}

public static class StatusExtensions
{
    /// <summary>Human-readable description, matching the Python client wording.</summary>
    public static string Describe(this Status status) => status switch
    {
        Status.HfTagOk => Strings.Status_HfOk,
        Status.HfTagNo => Strings.Status_HfNo,
        Status.HfErrStat => Strings.Status_HfErrStat,
        Status.HfErrCrc => Strings.Status_HfErrCrc,
        Status.HfCollision => Strings.Status_HfCollision,
        Status.HfErrBcc => Strings.Status_HfErrBcc,
        Status.MfErrAuth => Strings.Status_MfErrAuth,
        Status.HfErrParity => Strings.Status_HfErrParity,
        Status.HfErrAts => Strings.Status_HfErrAts,
        Status.LfTagOk => Strings.Status_LfOk,
        Status.LfTagNoFound => Strings.Status_LfNotFound,
        Status.ParErr => Strings.Status_ParErr,
        Status.DeviceModeError => Strings.Status_ModeErr,
        Status.InvalidCmd => Strings.Status_InvalidCmd,
        Status.Success => Strings.Status_Success,
        Status.NotImplemented => Strings.Status_NotImplemented,
        Status.FlashWriteFail => Strings.Status_FlashWrite,
        Status.FlashReadFail => Strings.Status_FlashRead,
        Status.InvalidSlotType => Strings.Status_InvalidSlotType,
        _ => string.Format(Strings.Status_Unknown, (ushort)status),
    };

    public static bool IsOk(this Status status) =>
        status is Status.Success or Status.HfTagOk or Status.LfTagOk;
}
