using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ChameLab.Desktop;

/// <summary>
/// Maps a COM port name to its USB VID on Windows by walking
/// <c>HKLM\SYSTEM\CurrentControlSet\Enum\USB</c>. Used to flag Chameleon
/// devices (VID 6868) in the connection list, and to find the bootloader (1915:521F).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsSerialVid
{
    public static bool PortMatchesVid(string portName, string vidHex) =>
        PortMatchesUsbId(portName, $"VID_{vidHex}");

    /// <summary>
    /// True when a currently present USB device matching <paramref name="usbId"/> owns the port.
    /// The Enum key keeps every device ever seen, and SERIALCOMM can keep listing a port after
    /// its device was unplugged, so presence is checked against the PnP tree.
    /// </summary>
    /// <param name="usbId">Device key fragment such as <c>VID_1915&amp;PID_521F</c>.</param>
    public static bool PortMatchesUsbId(string portName, string usbId)
    {
        using var usb = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is null) return false;

        foreach (var deviceKeyName in usb.GetSubKeyNames())
        {
            if (!deviceKeyName.Contains(usbId, StringComparison.OrdinalIgnoreCase))
                continue;

            using var deviceKey = usb.OpenSubKey(deviceKeyName);
            if (deviceKey is null) continue;

            foreach (var instanceName in deviceKey.GetSubKeyNames())
            {
                using var paramsKey = deviceKey.OpenSubKey(
                    $@"{instanceName}\Device Parameters");
                if (paramsKey?.GetValue("PortName") is string pn &&
                    string.Equals(pn, portName, StringComparison.OrdinalIgnoreCase) &&
                    IsPresent($@"USB\{deviceKeyName}\{instanceName}"))
                    return true;
            }
        }
        return false;
    }

    private const int CR_SUCCESS = 0;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0;

    /// <summary>A device instance is plugged in: locating it without the PHANTOM flag fails otherwise.</summary>
    private static bool IsPresent(string instanceId) =>
        CM_Locate_DevNodeW(out _, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
}
