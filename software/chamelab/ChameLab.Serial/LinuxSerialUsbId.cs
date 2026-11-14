using System;
using System.Globalization;
using System.IO;

namespace ChameLab.Transport.Serial;

/// <summary>Maps a Linux tty (e.g. /dev/ttyACM0) to the USB VID/PID of the device behind it.</summary>
internal static class LinuxSerialUsbId
{
    /// <summary>
    /// Reads the ids from /sys/class/tty/&lt;tty&gt;/device/uevent. For a CDC-ACM port, device is
    /// the USB interface, whose uevent carries PRODUCT=vid/pid/bcd. Reading through the links lets
    /// the kernel resolve them; walking up with ".." from a resolved link target does not work in
    /// .NET, which collapses ".." textually across the /sys/class symlinks and ends up outside the
    /// device tree.
    /// </summary>
    /// <param name="sysRoot">sysfs mount point; tests point it at a fake tree.</param>
    public static (int Vid, int Pid)? Read(string portName, string sysRoot = "/sys")
    {
        var tty = Path.GetFileName(portName);
        if (string.IsNullOrEmpty(tty)) return null;
        try
        {
            return Parse(File.ReadAllText(Path.Combine(sysRoot, "class", "tty", tty, "device", "uevent")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the ids from a USB interface's uevent. The kernel writes PRODUCT as unpadded hex
    /// (483/5740/200 for 0483:5740), so the ids are compared as numbers, not strings.
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
