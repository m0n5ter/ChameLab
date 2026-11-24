using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Mobile;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Xunit;
using static ChameLab.Tests.BleDfuTests;

namespace ChameLab.Tests;

public class BleTransportTests
{
    private static readonly Guid NusService = new("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly Guid NusRx = new("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly Guid NusTx = new("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");

    [Theory]
    [InlineData(false, 247, 244)]
    [InlineData(false, 517, 244)] // Android 14+ may report 517; cap to Chameleon NUS payload limit
    [InlineData(true, 185, 185)]
    public async Task ConnectNegotiatesMtuAfterStartUpdatesAndCapsPayload(bool isApple, int reportedMtu, int expectedPayload)
    {
        var calls = new List<string>();
        var (adapter, device, _) = CreateRig(calls, reportedMtu: reportedMtu);

        await using var transport = new BleTransport(
            device.Object.Id, "Chameleon Ultra", adapter.Object, device.Object,
            waitForScan: _ => Task.CompletedTask,
            isApple: isApple);

        await transport.ConnectAsync();

        Assert.Equal(expectedPayload, transport.MtuPayload);
        Assert.Contains("ConnectToDevice", calls);
        Assert.DoesNotContain("ConnectToKnownDevice", calls);
        Assert.True(calls.IndexOf("StartUpdates") < calls.IndexOf("RequestMtu"),
            "MTU negotiation must happen after GATT discovery and CCCD setup.");
    }

    [Fact]
    public async Task HangingRequestMtuFallsBackToDefaultPayloadInsteadOfHanging()
    {
        var calls = new List<string>();
        var never = new TaskCompletionSource<int>();
        var (adapter, device, _) = CreateRig(calls, requestMtuTask: never.Task);

        await using var transport = new BleTransport(
            device.Object.Id, "Chameleon Ultra", adapter.Object, device.Object,
            waitForScan: _ => Task.CompletedTask,
            isApple: false,
            mtuTimeout: TimeSpan.FromMilliseconds(50));

        await transport.ConnectAsync();

        Assert.Equal(20, transport.MtuPayload);
        Assert.Contains("RequestMtu", calls);
    }

    [Fact]
    public async Task HangingConnectTimesOutWithClearError()
    {
        var calls = new List<string>();
        var never = new TaskCompletionSource();
        var (adapter, device, _) = CreateRig(calls, connectTask: never.Task);

        await using var transport = new BleTransport(
            device.Object.Id, "Chameleon Ultra", adapter.Object, device.Object,
            waitForScan: _ => Task.CompletedTask,
            connectTimeout: TimeSpan.FromMilliseconds(50),
            disposeTimeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => transport.ConnectAsync());

        Assert.Equal(Strings.Ble_ConnectTimeout, ex.Message);
    }

    [Fact]
    public async Task HangingStartUpdatesTimesOutWithClearError()
    {
        var calls = new List<string>();
        var never = new TaskCompletionSource();
        var (adapter, device, _) = CreateRig(calls, startUpdatesTask: never.Task);

        await using var transport = new BleTransport(
            device.Object.Id, "Chameleon Ultra", adapter.Object, device.Object,
            waitForScan: _ => Task.CompletedTask,
            startUpdatesTimeout: TimeSpan.FromMilliseconds(50),
            disposeTimeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => transport.ConnectAsync());

        Assert.Equal(Strings.Ble_ConnectTimeout, ex.Message);
    }

    [Fact]
    public async Task HangingScanWaitDoesNotBlockConnect()
    {
        var calls = new List<string>();
        var never = new TaskCompletionSource();
        var (adapter, device, _) = CreateRig(calls, reportedMtu: 247);

        await using var transport = new BleTransport(
            device.Object.Id, "Chameleon Ultra", adapter.Object, device.Object,
            waitForScan: _ => never.Task,
            isApple: false,
            scanWaitTimeout: TimeSpan.FromMilliseconds(50));

        await transport.ConnectAsync();

        Assert.Equal(244, transport.MtuPayload);
    }

    [Fact]
    public async Task HangingDisposeCallbacksDoNotHangCleanup()
    {
        var calls = new List<string>();
        var neverStop = new TaskCompletionSource();
        var neverDisconnect = new TaskCompletionSource();
        var (adapter, device, _) = CreateRig(calls, stopUpdatesTask: neverStop.Task, disconnectTask: neverDisconnect.Task);

        var transport = new BleTransport(
            device.Object.Id, "Chameleon Ultra", adapter.Object, device.Object,
            waitForScan: _ => Task.CompletedTask,
            disposeTimeout: TimeSpan.FromMilliseconds(50));

        await transport.ConnectAsync();
        await transport.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains("StopUpdates", calls);
        Assert.Contains("DisconnectDevice", calls);
    }

    private static (Handle<IAdapter> Adapter, Handle<IDevice> Device, Handle<ICharacteristic> Tx) CreateRig(
        List<string> calls,
        int reportedMtu = 247,
        Task? connectTask = null,
        Task? startUpdatesTask = null,
        Task<int>? requestMtuTask = null,
        Task? stopUpdatesTask = null,
        Task? disconnectTask = null)
    {
        var adapter = FakeProxy.Create<IAdapter>();
        var device = FakeProxy.Create<IDevice>();
        var service = FakeProxy.Create<IService>();
        var rx = FakeProxy.Create<ICharacteristic>();
        var tx = FakeProxy.Create<ICharacteristic>();

        device.Values["Id"] = Guid.NewGuid();
        device.Values["Name"] = "Chameleon Ultra";
        device.Values["State"] = DeviceState.Connected;

        tx.OnMethod = (name, _) =>
        {
            if (name == "StartUpdatesAsync")
            {
                calls.Add("StartUpdates");
                return startUpdatesTask ?? Task.CompletedTask;
            }
            if (name == "StopUpdatesAsync")
            {
                calls.Add("StopUpdates");
                return stopUpdatesTask ?? Task.CompletedTask;
            }
            return null;
        };

        service.OnMethod = (name, args) =>
        {
            if (name == "GetCharacteristicAsync")
            {
                var id = (Guid)args[0]!;
                if (id == NusRx) return Task.FromResult(rx.Object);
                if (id == NusTx) return Task.FromResult(tx.Object);
            }
            return null;
        };

        device.OnMethod = (name, _) =>
        {
            if (name == "UpdateConnectionInterval")
            {
                calls.Add("UpdateConnectionInterval");
                return true;
            }
            if (name == "GetServiceAsync")
            {
                calls.Add("GetService");
                return Task.FromResult(service.Object);
            }
            if (name == "RequestMtuAsync")
            {
                calls.Add("RequestMtu");
                return requestMtuTask ?? Task.FromResult(reportedMtu);
            }
            return null;
        };

        adapter.OnMethod = (name, _) =>
        {
            if (name == "ConnectToDeviceAsync")
            {
                calls.Add("ConnectToDevice");
                return connectTask ?? Task.CompletedTask;
            }
            if (name == "ConnectToKnownDeviceAsync")
            {
                calls.Add("ConnectToKnownDevice");
                return connectTask is null
                    ? Task.FromResult(device.Object)
                    : connectTask.ContinueWith(_ => device.Object);
            }
            if (name == "DisconnectDeviceAsync")
            {
                calls.Add("DisconnectDevice");
                return disconnectTask ?? Task.CompletedTask;
            }
            return null;
        };

        return (adapter, device, tx);
    }
}
