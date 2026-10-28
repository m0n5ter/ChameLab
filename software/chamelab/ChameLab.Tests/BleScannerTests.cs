using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Mobile;
using ChameLab.Transport;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Xunit;

namespace ChameLab.Tests;

public class BleScannerTests
{
    [Fact]
    public async Task WatchRecoversAfterScanFailureAndAcceptsLaterAdvertisement()
    {
        var adapter = Proxy.Create<IAdapter>();
        var ble = Proxy.Create<IBluetoothLE>();
        ble.Values["Adapter"] = adapter.Object;
        ble.Values["IsAvailable"] = true;
        ble.Values["IsOn"] = true;
        var device = Proxy.Create<IDevice>();
        device.Values["Id"] = Guid.NewGuid();
        device.Values["Name"] = "";
        device.Values["Rssi"] = -42;
        var scans = 0;
        adapter.Call = async args =>
        {
            if (Interlocked.Increment(ref scans) == 1) throw new InvalidOperationException("Transient scan failure");
            Assert.Equal(ScanMode.LowLatency, adapter.Values["ScanMode"]);
            Assert.True((bool)args[2]!); // request repeated advertisements
            adapter.Advertise(device.Object); // no identifying data yet
            device.Values["Name"] = "Chameleon Ultra";
            adapter.Advertise(device.Object); // later scan response identifies the same device
            await Task.Delay(Timeout.Infinite, (CancellationToken)args[3]!);
        };
        var scanner = new BleScanner(ble.Object);
        var seen = new TaskCompletionSource<TransportCandidate>(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.StartWatching(found => { if (found.Count > 0) seen.TrySetResult(found[0]); });
        try
        {
            var candidate = await seen.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("Chameleon Ultra", candidate.DisplayName);
            Assert.Equal(device.Values["Id"]!.ToString(), candidate.Id);
            Assert.Equal(2, scans);
        }
        finally
        {
            scanner.StopWatching();
            await BleScanner.WaitForScanAsync(CancellationToken.None);
        }
        Assert.False(adapter.HasAdvertisementSubscriber);
    }

    [Fact]
    public async Task ReopeningWatchWaitsForOldNativeCleanupAndContinuesScanning()
    {
        var adapter = Proxy.Create<IAdapter>();
        var ble = Proxy.Create<IBluetoothLE>();
        ble.Values["Adapter"] = adapter.Object;
        ble.Values["IsAvailable"] = true;
        ble.Values["IsOn"] = true;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scans = 0;
        adapter.Call = async args =>
        {
            var first = Interlocked.Increment(ref scans) == 1;
            if (first) started.TrySetResult();
            else restarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, (CancellationToken)args[3]!); }
            catch (OperationCanceledException) { if (first) await cleanup.Task; }
        };
        var scanner = new BleScanner(ble.Object);
        scanner.StartWatching(_ => { });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            scanner.StopWatching();
            scanner.StartWatching(_ => { });
            Assert.False(restarted.Task.IsCompleted);
            cleanup.TrySetResult();
            await restarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, scans);
        }
        finally
        {
            cleanup.TrySetResult();
            scanner.StopWatching();
            await BleScanner.WaitForScanAsync(CancellationToken.None);
        }
        Assert.False(adapter.HasAdvertisementSubscriber);
    }

    // Only the BLE API boundary is faked; tests run the production scan/watch lifecycle.
    public class Proxy : DispatchProxy
    {
        public Dictionary<string, object?> Values { get; } = new();
        public Func<object?[], Task>? Call;
        private EventHandler<DeviceEventArgs>? _advertised;
        public bool HasAdvertisementSubscriber => _advertised is not null;
        public void Advertise(IDevice device) => _advertised?.Invoke(this, new DeviceEventArgs { Device = device });

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var name = method!.Name;
            args ??= Array.Empty<object?>();
            if (name == "add_DeviceAdvertised") { _advertised += (EventHandler<DeviceEventArgs>)args[0]!; return null; }
            if (name == "remove_DeviceAdvertised") { _advertised -= (EventHandler<DeviceEventArgs>)args[0]!; return null; }
            if (name.StartsWith("get_"))
                return Values.TryGetValue(name[4..], out var value) ? value :
                    method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
            if (name.StartsWith("set_")) { Values[name[4..]] = args[0]; return null; }
            if (name == "StartScanningForDevicesAsync") return Call!(args);
            if (name == "StopScanningForDevicesAsync") return Task.CompletedTask;
            throw new NotSupportedException(name);
        }

        public static Handle<T> Create<T>() where T : class
        {
            var obj = DispatchProxy.Create<T, Proxy>();
            return new Handle<T>(obj, (Proxy)(object)obj);
        }
    }

    public record Handle<T>(T Object, Proxy Handler)
    {
        public Dictionary<string, object?> Values => Handler.Values;
        public Func<object?[], Task>? Call { set => Handler.Call = value; }
        public void Advertise(IDevice device) => Handler.Advertise(device);
        public bool HasAdvertisementSubscriber => Handler.HasAdvertisementSubscriber;
    }
}
