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
        Status.HfTagOk => "HF tag operation succeeded",
        Status.HfTagNo => "HF tag not found or lost",
        Status.HfErrStat => "HF tag status error",
        Status.HfErrCrc => "HF tag data CRC error",
        Status.HfCollision => "HF tag collision",
        Status.HfErrBcc => "HF tag UID BCC error",
        Status.MfErrAuth => "HF tag auth failed",
        Status.HfErrParity => "HF tag data parity error",
        Status.HfErrAts => "HF tag was supposed to send ATS but didn't",
        Status.LfTagOk => "LF tag operation succeeded",
        Status.LfTagNoFound => "LF tag not found",
        Status.ParErr => "API request failed: parameter error",
        Status.DeviceModeError => "API request failed: device mode error",
        Status.InvalidCmd => "API request failed: command invalid",
        Status.Success => "Device operation succeeded",
        Status.NotImplemented => "API not implemented",
        Status.FlashWriteFail => "Flash write failed",
        Status.FlashReadFail => "Flash read failed",
        Status.InvalidSlotType => "Invalid card type in slot",
        _ => $"Unknown status 0x{(ushort)status:X2}",
    };

    public static bool IsOk(this Status status) =>
        status is Status.Success or Status.HfTagOk or Status.LfTagOk;
}
