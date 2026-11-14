using System;
using System.Globalization;
using System.IO;

namespace ChameLab.Transport.Serial;

/// <summary>
/// Maps a Linux tty (e.g. <c>/dev/ttyACM0</c>) to the USB vendor and product id of the device
/// behind it. Used to flag Chameleon devices (VID 6868) in the connection list, and to find the
/// bootloader (1915:521F).
/// </summary>
internal static class LinuxSerialUsbId
{
    /// <summary>
    /// Reads the ids from <c>/sys/class/tty/&lt;tty&gt;/device/uevent</c>. For a CDC-ACM port,
    /// <c>device</c> is the USB interface, whose uevent carries <c>PRODUCT=vid/pid/bcdDevice</c>.
    /// </summary>
    /// <remarks>
    /// The path is handed to the kernel as-is so it follows the sysfs symlinks itself. Resolving
    /// them in managed code goes wrong: <c>/sys/class/tty/ttyACM0</c> is itself a symlink, and
    /// .NET joins the relative target of <c>device</c> (<c>../../../1-4:1.0</c>) onto the lexical
    /// link path, which lands on <c>/sys/1-4:1.0</c> instead of the USB interface.
    /// </remarks>
    /// <param name="portName">Port name or device path; only the file name is used.</param>
    /// <param name="sysRoot">sysfs mount point; tests point this at a fake tree.</param>
    /// <returns>The ids, or null when the port is not a USB interface or sysfs cannot be read.</returns>
    public static (int Vid, int Pid)? Read(string portName, string sysRoot = "/sys")
    {
        var tty = Path.GetFileName(portName);
        if (string.IsNullOrEmpty(tty)) return null;
        var uevent = Path.Combine(sysRoot, "class", "tty", tty, "device", "uevent");
        try
        {
            return Parse(File.ReadAllText(uevent));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the ids from a USB interface's uevent text. The kernel writes <c>PRODUCT</c> as
    /// unpadded lowercase hex (<c>PRODUCT=483/5740/200</c> for 0483:5740), so the values are
    /// compared as numbers, not strings.
    /// </summary>
    internal static (int Vid, int Pid)? Parse(string uevent)
    {
        foreach (var line in uevent.Split('\n'))
        {
            if (!line.StartsWith("PRODUCT=", StringComparison.Ordinal)) continue;
            var parts = line["PRODUCT=".Length..].Trim().Split('/');
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var vid) &&
                int.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var pid))
                return (vid, pid);
            return null;
        }
        return null;
    }
}
