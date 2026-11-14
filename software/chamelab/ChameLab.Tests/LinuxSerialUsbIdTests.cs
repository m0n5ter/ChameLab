using System;
using System.IO;
using ChameLab.Transport.Serial;
using Xunit;

namespace ChameLab.Tests;

// Contributed by @fooberticus in https://github.com/m0n5ter/ChameLab/pull/5.
public class LinuxSerialUsbIdTests
{
    private const string ChameleonInterfaceUevent =
        "DEVTYPE=usb_interface\nDRIVER=cdc_acm\nPRODUCT=6868/8686/200\nTYPE=0/0/0\nINTERFACE=2/2/1\n" +
        "MODALIAS=usb:v6868p8686d0200dc00dsc00dp00ic02isc02ip01in00\n";

    [Theory]
    [InlineData(ChameleonInterfaceUevent, 0x6868, 0x8686)]
    [InlineData("DEVTYPE=usb_interface\nPRODUCT=1915/521f/100\n", 0x1915, 0x521F)]
    [InlineData("PRODUCT=483/5740/200\n", 0x0483, 0x5740)] // the kernel does not zero-pad
    public void ParseReadsVendorAndProductFromTheInterfaceUevent(string uevent, int vid, int pid) =>
        Assert.Equal((vid, pid), LinuxSerialUsbId.Parse(uevent));

    [Theory]
    [InlineData("")]
    [InlineData("DRIVER=serial8250\n")]
    [InlineData("PRODUCT=zz/8686/200\n")]
    [InlineData("PRODUCT=6868\n")]
    public void ParseRejectsUeventsWithoutAUsbProduct(string uevent) =>
        Assert.Null(LinuxSerialUsbId.Parse(uevent));

    [Fact]
    public void ReadFollowsTheClassAndDeviceSymlinksTheWayTheKernelDoes()
    {
        // Real sysfs layout: /sys/class/tty/ttyACM0 is a symlink into /sys/devices, and its
        // `device` link is relative to that real location. Resolving `device` against the lexical
        // path /sys/class/tty/ttyACM0 lands on /sys/1-4:1.0, which is how detection used to fail.
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("chamelab-sysfs-").FullName;
        try
        {
            var usbDevice = Path.Combine(root, "devices", "pci0000:00", "0000:00:14.0", "usb1", "1-4");
            var iface = Path.Combine(usbDevice, "1-4:1.0");
            var ttyNode = Path.Combine(iface, "tty", "ttyACM0");
            Directory.CreateDirectory(ttyNode);
            File.WriteAllText(Path.Combine(usbDevice, "idVendor"), "6868\n");
            File.WriteAllText(Path.Combine(iface, "uevent"), ChameleonInterfaceUevent);
            Directory.CreateSymbolicLink(Path.Combine(ttyNode, "device"), "../../../1-4:1.0");
            Directory.CreateDirectory(Path.Combine(root, "class", "tty"));
            Directory.CreateSymbolicLink(Path.Combine(root, "class", "tty", "ttyACM0"),
                "../../devices/pci0000:00/0000:00:14.0/usb1/1-4/1-4:1.0/tty/ttyACM0");

            Assert.Equal((0x6868, 0x8686), LinuxSerialUsbId.Read("/dev/ttyACM0", root));
            Assert.Null(LinuxSerialUsbId.Read("/dev/ttyACM1", root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
