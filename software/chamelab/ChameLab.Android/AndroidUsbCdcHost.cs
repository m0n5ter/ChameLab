using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using ChameLab.Mobile;

namespace ChameLab.Android;

/// <summary>Android USB Host access for the Chameleon's CDC ACM interfaces; no serial driver package needed.</summary>
internal sealed class AndroidUsbCdcHost : IUsbCdcHost
{
    private static Context Context => global::Android.App.Application.Context;
    private static UsbManager? Manager => (UsbManager?)Context.GetSystemService(Context.UsbService);

    public IReadOnlyList<UsbCdcDevice> GetDevices()
    {
        var result = new List<UsbCdcDevice>();
        var devices = Manager?.DeviceList;
        if (devices is null) return result;
        foreach (var device in devices.Values)
        {
            if (device.VendorId != 0x6868) continue;
            result.Add(new UsbCdcDevice(device.DeviceName, $"Chameleon (USB {device.DeviceId})",
                device.VendorId, FindPort(device) is not null));
        }
        return result;
    }

    public async Task<IUsbCdcConnection> OpenAsync(string id, CancellationToken ct)
    {
        var manager = Manager ?? throw new NotSupportedException("This phone does not support USB Host mode.");
        if (manager.DeviceList is not { } devices || !devices.TryGetValue(id, out var device) || device.VendorId != 0x6868)
            throw new IOException("The Chameleon USB device is no longer attached.");
        var port = FindPort(device) ?? throw new IOException("The USB device has no CDC ACM serial interface.");
        await RequestPermissionAsync(manager, device, ct);
        // Claiming and control transfers must not block the UI thread.
        return await Task.Run<IUsbCdcConnection>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var native = manager.OpenDevice(device) ?? throw new IOException("Could not open the USB device. Reconnect it and allow USB access.");
            var connection = new Connection(manager, device, native, port);
            try
            {
                connection.Configure();
                ct.ThrowIfCancellationRequested();
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }, ct);
    }

    private static async Task RequestPermissionAsync(UsbManager manager, UsbDevice device, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (manager.HasPermission(device)) return;
        var action = Context.PackageName + ".USB_PERMISSION." + Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var receiver = new PermissionReceiver(intent =>
        {
            if (intent?.Action == action) completion.TrySetResult(manager.HasPermission(device));
            else if (intent?.Action == UsbManager.ActionUsbDeviceDetached
                     && (manager.DeviceList is not { } devices || !devices.ContainsKey(device.DeviceName)))
                completion.TrySetException(new IOException("The USB device was unplugged while waiting for permission."));
        });
        using var filter = new IntentFilter(action);
        filter.AddAction(UsbManager.ActionUsbDeviceDetached);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            Context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
        else
            Context.RegisterReceiver(receiver, filter);
        try
        {
            // Bind this request to its own receiver/device. Immutable intents need no result extras:
            // re-check the actual permission grant, rather than trusting a broadcast payload.
            using var intent = new Intent(action).SetPackage(Context.PackageName);
            using var pending = PendingIntent.GetBroadcast(Context, 0, intent, PendingIntentFlags.Immutable)!;
            try
            {
                manager.RequestPermission(device, pending);
                if (!await completion.Task.WaitAsync(TimeSpan.FromMinutes(2), ct))
                    throw new UnauthorizedAccessException("USB access was denied. Select the device again and allow USB access.");
            }
            finally { pending.Cancel(); }
        }
        finally { Context.UnregisterReceiver(receiver); }
    }

    private sealed class PermissionReceiver(Action<Intent?> received) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            received(intent);
        }
    }

    private sealed record Port(UsbInterface Control, UsbInterface Data, UsbEndpoint Input, UsbEndpoint Output);

    private static Port? FindPort(UsbDevice device)
    {
        // The firmware exposes CDC control interface 0 and data interface 1. Use descriptor IDs
        // instead of array positions so endpoint ordering is immaterial.
        UsbInterface? control = null, data = null;
        UsbEndpoint? input = null, output = null;
        for (var i = 0; i < device.InterfaceCount; i++)
        {
            var iface = device.GetInterface(i);
            if (iface is null) continue;
            if ((int)iface.InterfaceClass == 2 && (int)iface.InterfaceSubclass == 2) control = iface;
            if ((int)iface.InterfaceClass != 10) continue;
            UsbEndpoint? read = null, write = null;
            for (var e = 0; e < iface.EndpointCount; e++)
            {
                var endpoint = iface.GetEndpoint(e);
                if (endpoint is null) continue;
                if (endpoint.Type != UsbAddressing.XferBulk) continue;
                if (endpoint.Direction == UsbAddressing.In) read = endpoint;
                else write = endpoint;
            }
            if (read is not null && write is not null)
            {
                data = iface;
                input = read;
                output = write;
            }
        }
        return control is not null && data is not null && input is not null && output is not null
            ? new Port(control, data, input, output) : null;
    }

    private sealed class Connection(UsbManager manager, UsbDevice device, UsbDeviceConnection native, Port port) : IUsbCdcConnection
    {
        // Reads and writes may run together. Closing waits for their bounded timeouts before
        // releasing the native handle, so unplug/dispose cannot race Java wrapper disposal.
        private readonly ReaderWriterLockSlim _lifetime = new();
        private bool _closed, _controlClaimed, _dataClaimed, _portEnabled;

        public bool IsAttached => !Volatile.Read(ref _closed)
            && manager.DeviceList is { } devices && devices.TryGetValue(device.DeviceName, out var attached)
            && attached.DeviceId == device.DeviceId;

        public void Configure()
        {
            _controlClaimed = native.ClaimInterface(port.Control, true);
            if (!_controlClaimed) throw new IOException("Could not claim the USB CDC control interface.");
            _dataClaimed = native.ClaimInterface(port.Data, true);
            if (!_dataClaimed) throw new IOException("Could not claim the USB CDC data interface.");
            // CDC SET_LINE_CODING: 115200 baud (little-endian), one stop bit, no parity, 8 bits.
            byte[] coding = [0x00, 0xc2, 0x01, 0x00, 0x00, 0x00, 0x08];
            if (native.ControlTransfer((UsbAddressing)0x21, 0x20, 0, port.Control.Id, coding, coding.Length, 1000) != coding.Length)
                throw new IOException("Could not configure the USB serial port.");
            // DTR wakes the firmware's CDC endpoint; RTS is asserted as on a normal serial port.
            if (native.ControlTransfer((UsbAddressing)0x21, 0x22, 3, port.Control.Id, null, 0, 1000) < 0)
                throw new IOException("Could not enable the USB serial port.");
            _portEnabled = true;
        }

        public int Read(byte[] buffer, int timeoutMs) => Transfer(port.Input, buffer, 0, buffer.Length, timeoutMs);
        public int Write(byte[] buffer, int offset, int count, int timeoutMs) => Transfer(port.Output, buffer, offset, count, timeoutMs);

        private int Transfer(UsbEndpoint endpoint, byte[] buffer, int offset, int count, int timeoutMs)
        {
            _lifetime.EnterReadLock();
            try
            {
                if (_closed) throw new IOException("The USB connection was closed.");
                return native.BulkTransfer(endpoint, buffer, offset, count, timeoutMs);
            }
            finally { _lifetime.ExitReadLock(); }
        }

        public void Dispose()
        {
            _lifetime.EnterWriteLock();
            try
            {
                if (_closed) return;
                _closed = true;
                try
                {
                    if (_portEnabled)
                        native.ControlTransfer((UsbAddressing)0x21, 0x22, 0, port.Control.Id, null, 0, 100);
                    if (_dataClaimed) native.ReleaseInterface(port.Data);
                    if (_controlClaimed) native.ReleaseInterface(port.Control);
                }
                finally
                {
                    native.Close();
                    native.Dispose();
                }
            }
            finally { _lifetime.ExitWriteLock(); }
        }
    }
}
