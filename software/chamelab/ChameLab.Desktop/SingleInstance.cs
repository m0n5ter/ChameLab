using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChameLab.Desktop;

/// <summary>
/// Keeps only one desktop instance of ChameLab running per data directory and forwards subsequent
/// launches over a per-user named pipe / Unix domain socket so the running instance activates its window.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const byte CommandActivate = 1;
    private const byte AckOk = 1;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly FileStream? _lockFile;
    private readonly string? _pipeName;
    private readonly Task? _acceptTask;

    private NamedPipeServerStream? _server;
    private Func<Task>? _onActivate;
    private bool _pendingActivation;
    private int _disposed;

    private SingleInstance(FileStream? lockFile, string? pipeName, NamedPipeServerStream? server)
    {
        _lockFile = lockFile;
        _pipeName = pipeName;
        _server = server;
        if (lockFile is not null && pipeName is not null)
            _acceptTask = Task.Run(AcceptLoopAsync);
    }

    internal static string DefaultLockDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChameLab");

    internal static string PipeNameFor(string directory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        var hash = Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
        if (OperatingSystem.IsWindows()) return $"chamelab-{hash}";

        var runtimeDir = OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") : null;
        var socketDir = !string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir)
            ? runtimeDir
            : Path.GetTempPath();
        var fileName = $"chamelab-{hash}.sock";
        var path = Path.Combine(socketDir, fileName);
        if (Encoding.UTF8.GetByteCount(path) > 95) path = Path.Combine("/tmp", fileName);
        return path;
    }

    public static SingleInstance? TryAcquire(bool activateExisting = true, string? directory = null)
    {
        var dir = directory ?? DefaultLockDirectory;
        var lockPath = Path.Combine(dir, "instance.lock");
        var pipeName = PipeNameFor(dir);

        FileStream? lockFile;
        try
        {
            Directory.CreateDirectory(dir);
            lockFile = OpenLockFile(lockPath);
        }
        catch (UnauthorizedAccessException)
        {
            return new SingleInstance(null, null, null);
        }
        catch (IOException)
        {
            if (!activateExisting) return null;
            if (TrySignalExisting(pipeName)) return null;

            // The lock owner may have been shutting down after closing its pipe; retry once.
            Thread.Sleep(150);
            try
            {
                lockFile = OpenLockFile(lockPath);
            }
            catch (UnauthorizedAccessException)
            {
                return new SingleInstance(null, null, null);
            }
            catch (IOException)
            {
                return null;
            }
        }

        var server = CreateServer(pipeName);
        return new SingleInstance(lockFile, pipeName, server);
    }

    public void Subscribe(Func<Task> onActivate)
    {
        bool pending;
        lock (_gate)
        {
            _onActivate = onActivate;
            pending = _pendingActivation;
            _pendingActivation = false;
        }
        if (pending) _ = InvokeSafelyAsync(onActivate);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts.Cancel();
        NamedPipeServerStream? server;
        lock (_gate)
        {
            server = _server;
            _server = null;
        }
        try { server?.Dispose(); } catch { }
        try { _acceptTask?.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        _cts.Dispose();

        // Remove the Unix socket while still holding the exclusive file lock.
        if (!OperatingSystem.IsWindows() && _pipeName is not null)
        {
            try { File.Delete(_pipeName); } catch { }
        }

        // Keep instance.lock on disk to avoid the Unix unlink/flock race.
        _lockFile?.Dispose();
    }

    private static FileStream OpenLockFile(string lockPath) =>
        new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static NamedPipeServerStream? CreateServer(string pipeName)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) File.Delete(pipeName);
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TrySignalExisting(string pipeName)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect(2000);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var pidBuffer = new byte[sizeof(int)];
            client.ReadExactlyAsync(pidBuffer, cts.Token).AsTask().GetAwaiter().GetResult();
            var pid = BinaryPrimitives.ReadInt32LittleEndian(pidBuffer);
            if (OperatingSystem.IsWindows() && pid > 0) AllowSetForegroundWindow(pid);

            var command = new[] { CommandActivate };
            client.WriteAsync(command, cts.Token).AsTask().GetAwaiter().GetResult();
            client.FlushAsync(cts.Token).GetAwaiter().GetResult();

            // Wait briefly for the primary instance to process activation before this process exits
            // (which allows Windows foreground hand-off to complete cleanly).
            var ack = new byte[1];
            try
            {
                client.ReadExactlyAsync(ack, cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException)
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server;
            lock (_gate)
            {
                if (_disposed != 0 || _cts.IsCancellationRequested || _pipeName is null) return;
                server = _server ??= CreateServer(_pipeName);
            }

            if (server is null)
            {
                try { await Task.Delay(100, _cts.Token); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(_cts.Token);
                try
                {
                    await HandleClientAsync(server);
                }
                finally
                {
                    if (server.IsConnected)
                    {
                        try { server.Disconnect(); }
                        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                    }
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_server, server)) _server = null;
                }
                try { server.Dispose(); } catch { }

                try { await Task.Delay(100, _cts.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeout.Token);
        var ct = linked.Token;

        var pidBuffer = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(pidBuffer, Environment.ProcessId);
        await server.WriteAsync(pidBuffer, ct);
        await server.FlushAsync(ct);

        var command = new byte[1];
        try
        {
            await server.ReadExactlyAsync(command, ct);
        }
        catch (EndOfStreamException)
        {
            return;
        }

        if (command[0] == CommandActivate)
        {
            Func<Task>? handler;
            lock (_gate)
            {
                handler = _onActivate;
                if (handler is null) _pendingActivation = true;
            }
            if (handler is not null) await InvokeSafelyAsync(handler);
        }

        try
        {
            await server.WriteAsync(new[] { AckOk }, ct);
            await server.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
        }
    }

    private static async Task InvokeSafelyAsync(Func<Task> handler)
    {
        try
        {
            await handler().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Handler errors or timeouts must never tear down the accept loop.
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
