using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Desktop;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public sealed class DesktopInstanceTests
{
    [Fact]
    public async Task SecondInstanceSignalsFirstAndExits()
    {
        var dir = CreateTempDir();
        try
        {
            using var first = SingleInstance.TryAcquire(activateExisting: true, dir);
            Assert.NotNull(first);

            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.Subscribe(() =>
            {
                activated.TrySetResult();
                return Task.CompletedTask;
            });

            using var second = SingleInstance.TryAcquire(activateExisting: true, dir);
            Assert.Null(second);

            await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task MultipleConsecutiveLaunchesAllActivateFirstInstance()
    {
        var dir = CreateTempDir();
        try
        {
            using var first = SingleInstance.TryAcquire(activateExisting: true, dir);
            Assert.NotNull(first);

            var count = 0;
            var allSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.Subscribe(() =>
            {
                if (Interlocked.Increment(ref count) == 3) allSeen.TrySetResult();
                return Task.CompletedTask;
            });

            for (var i = 0; i < 3; i++)
            {
                using var duplicate = SingleInstance.TryAcquire(activateExisting: true, dir);
                Assert.Null(duplicate);
            }

            await allSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, Volatile.Read(ref count));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task SecondBackgroundInstanceExitsWithoutActivating()
    {
        var dir = CreateTempDir();
        try
        {
            using var first = SingleInstance.TryAcquire(activateExisting: true, dir);
            Assert.NotNull(first);

            var count = 0;
            first.Subscribe(() =>
            {
                Interlocked.Increment(ref count);
                return Task.CompletedTask;
            });

            using var second = SingleInstance.TryAcquire(activateExisting: false, dir);
            Assert.Null(second);

            await Task.Delay(100);
            Assert.Equal(0, Volatile.Read(ref count));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task ActivationBeforeSubscribeIsDeliveredOnSubscribe()
    {
        var dir = CreateTempDir();
        try
        {
            using var first = SingleInstance.TryAcquire(activateExisting: true, dir);
            Assert.NotNull(first);

            using var second = SingleInstance.TryAcquire(activateExisting: true, dir);
            Assert.Null(second);

            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.Subscribe(() =>
            {
                activated.TrySetResult();
                return Task.CompletedTask;
            });

            await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task NewInstanceAcquiresLockAfterFirstDisposes()
    {
        var dir = CreateTempDir();
        try
        {
            var first = SingleInstance.TryAcquire(activateExisting: true, dir);
            Assert.NotNull(first);
            first.Dispose();

            using var replacement = SingleInstance.TryAcquire(activateExisting: true, dir);
            Assert.NotNull(replacement);

            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            replacement.Subscribe(() =>
            {
                activated.TrySetResult();
                return Task.CompletedTask;
            });

            using var duplicate = SingleInstance.TryAcquire(activateExisting: true, dir);
            Assert.Null(duplicate);

            await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void DesktopPlatformHelpersMatchAbiAndEscapeSafely()
    {
        Assert.Equal(IntPtr.Size == 8 ? 976 : 956, DesktopPlatform.NotifyIconDataSize);

        Assert.Equal("abc", DesktopPlatform.Truncate("abcdef", 3));
        Assert.Equal("abc", DesktopPlatform.Truncate("abc", 5));
        Assert.Equal(string.Empty, DesktopPlatform.Truncate(string.Empty, 5));
        // Avoid splitting a surrogate pair at the boundary.
        Assert.Equal("a", DesktopPlatform.Truncate("a\U0001F98E", 2));

        const string title = "ChameLab \"quote\" $(id)";
        const string message = "Line 1\n\"Line 2\" \\ $HOME";

        var mac = DesktopPlatform.BuildMacNotification(title, message);
        Assert.Equal("/usr/bin/osascript", mac.FileName);
        Assert.False(mac.UseShellExecute);
        Assert.True(mac.CreateNoWindow);
        Assert.Equal(5, mac.ArgumentList.Count);
        Assert.Equal("-e", mac.ArgumentList[0]);
        Assert.Equal("--", mac.ArgumentList[2]);
        Assert.Equal(title, mac.ArgumentList[3]);
        Assert.Equal(message, mac.ArgumentList[4]);

        var linux = DesktopPlatform.BuildLinuxNotification(title, message);
        Assert.Equal("notify-send", linux.FileName);
        Assert.False(linux.UseShellExecute);
        Assert.True(linux.CreateNoWindow);
        Assert.Equal(["-a", "ChameLab", "--", title, message], linux.ArgumentList);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ChameLab.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { }
    }
}

[Collection(AppSettingsCollection.Name)]
public sealed class TrayBalloonSettingsTests
{
    [Fact]
    public void TryConsumeFirstTrayHideReturnsTrueOnlyOnceAndPersists()
    {
        const string setting = "trayBalloonShown";
        var original = AppSettings.Get(setting);
        try
        {
            AppSettings.Set(setting, null);
            Assert.True(App.TryConsumeFirstTrayHide());
            Assert.True(AppSettings.Get(setting) is JsonValue saved && saved.GetValue<bool>());
            Assert.False(App.TryConsumeFirstTrayHide());

            AppSettings.Set(setting, false);
            Assert.True(App.TryConsumeFirstTrayHide());
            Assert.False(App.TryConsumeFirstTrayHide());
        }
        finally
        {
            AppSettings.Set(setting, original);
        }
    }
}
