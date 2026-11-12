using System;
using System.Threading.Tasks;
using ChameLab.Services;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

public class WakeLockTests
{
    [Fact]
    public async Task OverlappingOperationsShareTheLockUntilTheLastLeaseEnds()
    {
        var acquired = 0;
        var released = 0;
        var service = new WakeLockService(() =>
        {
            acquired++;
            return Task.FromResult<IAsyncDisposable?>(WakeLockService.ReleaseWith(() =>
            {
                released++;
                return ValueTask.CompletedTask;
            }));
        });

        var first = await service.AcquireAsync();
        var second = await service.AcquireAsync();
        Assert.Equal(1, acquired);
        await first.DisposeAsync();
        await first.DisposeAsync();
        Assert.Equal(0, released);
        await second.DisposeAsync();
        Assert.Equal(1, released);
        await using (await service.AcquireAsync()) Assert.Equal(2, acquired);
        Assert.Equal(2, released);
    }

    [Fact]
    public async Task ConcurrentAcquisitionWaitsForThePlatformLock()
    {
        var platformReady = new TaskCompletionSource<IAsyncDisposable?>();
        var acquired = 0;
        var released = 0;
        var service = new WakeLockService(() => { acquired++; return platformReady.Task; });
        var first = service.AcquireAsync();
        var second = service.AcquireAsync();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, acquired);

        platformReady.SetResult(WakeLockService.ReleaseWith(() =>
        {
            released++;
            return ValueTask.CompletedTask;
        }));
        var leases = await Task.WhenAll(first, second);
        await leases[1].DisposeAsync();
        Assert.Equal(0, released);
        await leases[0].DisposeAsync();
        Assert.Equal(1, released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PageHoldsLockThroughWorkAndCleanupIncludingErrors(bool fails)
    {
        var awake = false;
        var service = new WakeLockService(() =>
        {
            awake = true;
            return Task.FromResult<IAsyncDisposable?>(WakeLockService.ReleaseWith(() =>
            {
                awake = false;
                return ValueTask.CompletedTask;
            }));
        });
        var page = new TestPage(service);
        var finish = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var awakeAtStart = false;
        var awakeDuringCleanup = false;
        var task = page.Run(async () =>
        {
            try
            {
                awakeAtStart = awake;
                started.SetResult();
                await finish.Task;
                if (fails) throw new InvalidOperationException("Device disconnected");
            }
            finally { awakeDuringCleanup = awake; }
        });
        await started.Task;
        Assert.True(awake);
        finish.SetResult();
        await task;
        Assert.True(awakeAtStart);
        Assert.True(awakeDuringCleanup);
        Assert.False(awake);
    }

    [Fact]
    public async Task CancellationKeepsLockUntilDeviceCleanupHasFinished()
    {
        var awake = false;
        var service = new WakeLockService(() =>
        {
            awake = true;
            return Task.FromResult<IAsyncDisposable?>(WakeLockService.ReleaseWith(() =>
            {
                awake = false;
                return ValueTask.CompletedTask;
            }));
        });
        using var cts = new System.Threading.CancellationTokenSource();
        var cleanupStarted = new TaskCompletionSource();
        var cleanupFinished = new TaskCompletionSource();
        var operation = new TestPage(service).Run(async () =>
        {
            try { await Task.Delay(-1, cts.Token); }
            finally
            {
                cleanupStarted.SetResult();
                await cleanupFinished.Task;
            }
        });
        cts.Cancel();
        await cleanupStarted.Task;
        Assert.True(awake);
        cleanupFinished.SetResult();
        await operation;
        Assert.False(awake);
    }

    [Fact]
    public async Task PlatformFailuresDoNotPreventWorkOrFutureAcquisitions()
    {
        var attempts = 0;
        var releases = 0;
        var service = new WakeLockService(() =>
        {
            if (++attempts == 1) throw new NotSupportedException();
            return Task.FromResult<IAsyncDisposable?>(WakeLockService.ReleaseWith(() =>
            {
                releases++;
                throw new InvalidOperationException();
            }));
        });
        var worked = 0;
        var page = new TestPage(service);
        for (var i = 0; i < 3; i++)
            await page.Run(() => { worked++; return Task.CompletedTask; });
        Assert.Equal(3, worked);
        Assert.Equal(3, attempts);
        Assert.Equal(2, releases);
    }

    private sealed class TestPage(WakeLockService wakeLock) : PageViewModelBase(() => "Test", "", wakeLock)
    {
        public Task Run(Func<Task> work) => RunAwakeAsync(work);
        public Task RunOrdinary(Func<Task> work) => RunAsync(work);
    }

    [Fact]
    public async Task OrdinaryPageWorkDoesNotPreventSleep()
    {
        var acquired = false;
        var service = new WakeLockService(() =>
        {
            acquired = true;
            return Task.FromResult<IAsyncDisposable?>(null);
        });
        await new TestPage(service).RunOrdinary(() => Task.CompletedTask);
        Assert.False(acquired);
    }

    [Fact]
    public async Task WindowsPowerRequestCanBeAcquiredAndReleasedOnAnotherThread()
    {
        if (!OperatingSystem.IsWindows()) return;
        var lease = await Desktop.DesktopWakeLock.AcquireAsync();
        Assert.NotNull(lease);
        await Task.Run(async () => await lease.DisposeAsync());
    }
}
