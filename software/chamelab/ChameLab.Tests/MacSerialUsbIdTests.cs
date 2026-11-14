using ChameLab.Transport.Serial;
using Xunit;

namespace ChameLab.Tests;

public class MacSerialUsbIdTests
{
    // Shape of `ioreg -r -c IOUSBHostDevice -l -w0`: a hub with the bootloader behind it (so the
    // bootloader shows up twice, nested and as a root of its own), interfaces carrying ids as well.
    private const string Ioreg = """
        +-o USB2.0 Hub@01100000  <class IOUSBHostDevice, id 0x100000a01, registered, matched, active, busy 0 (5 ms), retain 20>
          | {
          |   "idProduct" = 1552
          |   "idVendor" = 2109
          | }
          |
          +-o Open DFU Bootloader@01110000  <class IOUSBHostDevice, id 0x100000b02, registered, matched, active, busy 0 (3 ms), retain 30>
            | {
            |   "idProduct" = 21023
            |   "idVendor" = 6421
            |   "USB Serial Number" = "E1C3A2B4D5F6"
            | }
            |
            +-o IOUSBHostInterface@0  <class IOUSBHostInterface, id 0x100000b05, registered, matched, active, busy 0 (1 ms), retain 8>
            | | {
            | |   "idProduct" = 21023
            | |   "idVendor" = 6421
            | | }
            | |
            | +-o AppleUSBACMControl  <class AppleUSBACMControl, id 0x100000b09, !registered, !matched, active, busy 0, retain 6>
            +-o IOUSBHostInterface@1  <class IOUSBHostInterface, id 0x100000b06, registered, matched, active, busy 0 (1 ms), retain 8>
              | {
              |   "idProduct" = 21023
              |   "idVendor" = 6421
              | }
              |
              +-o AppleUSBACMData  <class AppleUSBACMData, id 0x100000b0b, registered, matched, active, busy 0 (0 ms), retain 7>
                +-o IOModemSerialStreamSync  <class IOModemSerialStreamSync, id 0x100000b0d, registered, matched, active, busy 0 (0 ms), retain 6>
                  +-o IOSerialBSDClient  <class IOSerialBSDClient, id 0x100000b0f, registered, matched, active, busy 0 (0 ms), retain 6>
                      {
                        "IOCalloutDevice" = "/dev/cu.usbmodemE1C3A2B4D5F61"
                        "IODialinDevice" = "/dev/tty.usbmodemE1C3A2B4D5F61"
                      }

        +-o ChameleonUltra@02100000  <class IOUSBHostDevice, id 0x100000c01, registered, matched, active, busy 0 (5 ms), retain 30>
          | {
          |   "idProduct" = 34438
          |   "idVendor" = 26728
          | }
          |
          +-o IOUSBHostInterface@1  <class IOUSBHostInterface, id 0x100000c06, registered, matched, active, busy 0 (1 ms), retain 8>
            | {
            |   "idProduct" = 34438
            |   "idVendor" = 26728
            | }
            |
            +-o IOSerialBSDClient  <class IOSerialBSDClient, id 0x100000c0f, registered, matched, active, busy 0 (0 ms), retain 6>
                {
                  "IOCalloutDevice" = "/dev/cu.usbmodem21011"
                }

        """;

    [Fact]
    public void ParseMapsEachCalloutToTheNearestUsbDevice()
    {
        var ids = MacSerialUsbId.Parse(Ioreg);
        Assert.Equal(2, ids.Count);
        Assert.Equal((0x1915, 0x521F), ids["/dev/cu.usbmodemE1C3A2B4D5F61"]);
        Assert.Equal((0x6868, 0x8686), ids["/dev/cu.usbmodem21011"]);
    }

    [Fact]
    public void ParseIgnoresOutputWithoutSerialPorts() =>
        Assert.Empty(MacSerialUsbId.Parse("+-o Root  <class IORegistryEntry, id 0x100000100, retain 10>\n"));
}
