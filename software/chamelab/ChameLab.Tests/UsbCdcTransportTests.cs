using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Mobile;
using ChameLab.Protocol;
using ChameLab.Transport;
using Xunit;

namespace ChameLab.Tests;

public class UsbCdcTransportTests
{
    [Fact]
    public async Task DiscoveryFiltersOtherDevicesAndBootloadersWithoutOpeningOrRequestingPermission()
    {
        var host = new Host
        {
            Devices = [new("usb1", "Chameleon", 0x6868, true), new("usb2", "Other serial", 0x1234, true),
                new("usb3", "Not CDC", 0x6868, false), new("usb4", "DFU", 0x1915, true)],
        };
        var scanner = new UsbCdcScanner(host);
        var found = Assert.Single(await scanner.ScanAsync());
        Assert.Equal("usb1", found.Id);
        Assert.Equal(TransportKind.Serial, found.Kind);
        Assert.Equal(AutoScan.Poll, scanner.AutoScan);
        Assert.Equal(0, host.Opens);
    }

    [Fact]
    public async Task PartialWritesAndLargeFramesPreserveEveryByteInOrder()
    {
        var host = new Host();
        host.Connection.MaxWrite = 37;
        await using var transport = new UsbCdcTransport(host, "usb1", "Chameleon");
        await transport.ConnectAsync();
        var bytes = Enumerable.Range(0, 40000).Select(i => (byte)i).ToArray();
        await transport.WriteAsync(bytes);
        Assert.Equal(bytes, host.Connection.Written.ToArray());
        Assert.All(host.Connection.WriteSizes, size => Assert.InRange(size, 1, 16384));
    }

    [Fact]
    public async Task SmallReadsPreserveRemainderOfUsbPacket()
    {
        var host = new Host();
        host.Connection.Reads.Enqueue([1, 2, 3, 4, 5]);
        await using var transport = new UsbCdcTransport(host, "usb1", "Chameleon");
        await transport.ConnectAsync();
        var buffer = new byte[2];
        var result = new List<byte>();
        for (var i = 0; i < 3; i++)
            result.AddRange(buffer.Take(await transport.ReadAsync(buffer)));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, result);
        Assert.Equal(1, host.Connection.ReadCalls);
    }

    [Fact]
    public async Task IdleReadTimesOutWithoutDisconnecting()
    {
        var host = new Host();
        await using var transport = new UsbCdcTransport(host, "usb1", "Chameleon");
        await transport.ConnectAsync();
        Assert.Equal(0, await transport.ReadAsync(new byte[64]));
        Assert.True(transport.IsConnected);
    }

    [Fact]
    public async Task UnplugFailsAndClosesProtocolConnection()
    {
        var host = new Host();
        var unplugged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new UsbCdcTransport(host, "usb1", "Chameleon");
        await using var protocol = new ChameleonConnection(transport);
        protocol.Disconnected += _ => unplugged.TrySetResult();
        await protocol.OpenAsync();
        host.Connection.IsAttached = false;
        await unplugged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(protocol.IsClosed);
        Assert.False(transport.IsConnected);
        await Assert.ThrowsAsync<ChameleonDisconnectedException>(() => protocol.SendAsync(Command.GetAppVersion));
    }

    [Fact]
    public async Task FailedWritesThrowAndDisposeReleasesTheConnectionOnce()
    {
        var host = new Host();
        host.Connection.MaxWrite = -1;
        var transport = new UsbCdcTransport(host, "usb1", "Chameleon");
        await transport.ConnectAsync();
        await Assert.ThrowsAsync<IOException>(() => transport.WriteAsync(new byte[] { 1 }));
        await transport.DisposeAsync();
        await transport.DisposeAsync();
        Assert.Equal(1, host.Connection.Disposals);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task DeniedPermissionAndCanceledPermissionWaitLeaveNoConnection()
    {
        var host = new Host { Open = _ => throw new UnauthorizedAccessException("Denied") };
        await using var transport = new UsbCdcTransport(host, "usb1", "Chameleon");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transport.ConnectAsync());
        Assert.False(transport.IsConnected);

        using var canceled = new CancellationTokenSource();
        host.Open = async ct => { await Task.Delay(Timeout.Infinite, ct); return host.Connection; };
        var pending = transport.ConnectAsync(canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task DisposingWhilePermissionIsPendingCancelsOpen()
    {
        var host = new Host();
        host.Open = async ct => { await Task.Delay(Timeout.Infinite, ct); return host.Connection; };
        var transport = new UsbCdcTransport(host, "usb1", "Chameleon");
        var pending = transport.ConnectAsync();
        await transport.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task CanceledWriteDoesNotSendRemainingBytes()
    {
        var host = new Host();
        using var canceled = new CancellationTokenSource();
        host.Connection.MaxWrite = 1;
        host.Connection.AfterWrite = canceled.Cancel;
        await using var transport = new UsbCdcTransport(host, "usb1", "Chameleon");
        await transport.ConnectAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.WriteAsync(new byte[] { 1, 2, 3 }, canceled.Token));
        Assert.Equal(new byte[] { 1 }, host.Connection.Written);
    }

    private sealed class Host : IUsbCdcHost
    {
        public IReadOnlyList<UsbCdcDevice> Devices = [];
        public readonly Connection Connection = new();
        public Func<CancellationToken, Task<IUsbCdcConnection>>? Open;
        public int Opens;
        public IReadOnlyList<UsbCdcDevice> GetDevices() => Devices;
        public Task<IUsbCdcConnection> OpenAsync(string id, CancellationToken ct)
        {
            Opens++;
            return Open?.Invoke(ct) ?? Task.FromResult<IUsbCdcConnection>(Connection);
        }
    }

    private sealed class Connection : IUsbCdcConnection
    {
        public bool IsAttached { get; set; } = true;
        public int MaxWrite = int.MaxValue, ReadCalls, Disposals;
        public readonly Queue<byte[]> Reads = new();
        public readonly List<byte> Written = new();
        public readonly List<int> WriteSizes = new();
        public Action? AfterWrite;
        public int Read(byte[] buffer, int timeoutMs)
        {
            ReadCalls++;
            if (!Reads.TryDequeue(out var bytes)) { Thread.Sleep(1); return -1; }
            bytes.CopyTo(buffer, 0);
            return bytes.Length;
        }
        public int Write(byte[] buffer, int offset, int count, int timeoutMs)
        {
            WriteSizes.Add(count);
            var n = Math.Min(count, MaxWrite);
            if (n > 0) Written.AddRange(buffer.AsSpan(offset, n).ToArray());
            AfterWrite?.Invoke();
            return n;
        }
        public void Dispose() { IsAttached = false; Disposals++; }
    }
}
