using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ChameLab.Services;

/// <summary>
/// Extracts compressed native libraries embedded during NativeAOT publishing into a per-version
/// cache directory and resolves P/Invoke and OS dynamic library loads from it.
/// </summary>
public static class NativeLibraryBundle
{
    private const string Prefix = "ChameLab.NativeLibs.";
    private const string Suffix = ".gz";
    private const string HashResourceName = Prefix + "sha256";

    private static readonly object Gate = new();
    private static readonly HashSet<Assembly> RegisteredAssemblies = [];
    private static Dictionary<string, string>? _libraries;
    private static string? _cacheDir;
    private static bool _assemblyLoadHooked;

    /// <summary>
    /// Ensures any native libraries embedded in <paramref name="assembly"/> are extracted and
    /// registered for resolution. Returns the cache directory, or <see langword="null"/> when the
    /// build did not embed native libraries.
    /// </summary>
    public static string? EnsureExtracted(Assembly assembly)
    {
        lock (Gate)
        {
            if (_cacheDir is not null) return _cacheDir;

            using var hashStream = assembly.GetManifestResourceStream(HashResourceName);
            if (hashStream is null) return null;

            string hash;
            using (var reader = new StreamReader(hashStream))
                hash = reader.ReadToEnd().Trim();
            if (hash.Length == 0) return null;

            var entries = new List<(string ResourceName, string FileName)>();
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Suffix, StringComparison.Ordinal))
                    continue;
                var fileName = name[Prefix.Length..^Suffix.Length];
                if (fileName.Length == 0 || fileName.Contains('/') || fileName.Contains('\\') || fileName is "." or "..")
                    continue;
                entries.Add((name, fileName));
            }
            if (entries.Count == 0) return null;

            var dir = ExtractWithFallback(assembly, hash, entries);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, fileName) in entries)
            {
                var fullPath = Path.Combine(dir, fileName);
                map[fileName] = fullPath;
                var stem = Path.GetFileNameWithoutExtension(fileName);
                map.TryAdd(stem, fullPath);
                if (stem.StartsWith("lib", StringComparison.OrdinalIgnoreCase) && stem.Length > 3)
                    map.TryAdd(stem[3..], fullPath);
            }

            _libraries = map;
            _cacheDir = dir;

            if (OperatingSystem.IsWindows())
                SetDllDirectoryW(dir);

            foreach (var (_, fileName) in entries)
                NativeLibrary.TryLoad(Path.Combine(dir, fileName), out _);

            RegisterAllLoadedAssemblies();
            if (!_assemblyLoadHooked)
            {
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                _assemblyLoadHooked = true;
            }

            return _cacheDir;
        }
    }

    /// <summary>Returns the extracted path of an embedded native library, if present.</summary>
    public static string? TryGetLibraryPath(string fileName)
    {
        lock (Gate)
        {
            if (_libraries is null) return null;
            return _libraries.TryGetValue(fileName, out var path) ? path : null;
        }
    }

    private static string ExtractWithFallback(Assembly assembly, string hash, List<(string ResourceName, string FileName)> entries)
    {
        var primary = Path.Combine(CacheRoot(), hash);
        try
        {
            ExtractTo(assembly, primary, entries);
            return primary;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var fallback = Path.Combine(Path.GetTempPath(), "ChameLab", "native", hash);
            ExtractTo(assembly, fallback, entries);
            return fallback;
        }
    }

    private static void ExtractTo(Assembly assembly, string dir, List<(string ResourceName, string FileName)> entries)
    {
        Directory.CreateDirectory(dir);
        var ready = Path.Combine(dir, ".ready");
        if (File.Exists(ready) && entries.TrueForAll(e => File.Exists(Path.Combine(dir, e.FileName))))
            return;

        foreach (var (resourceName, fileName) in entries)
        {
            var target = Path.Combine(dir, fileName);
            if (File.Exists(target)) continue;

            using var compressed = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("Missing embedded native library: " + resourceName);
            var temp = target + ".tmp." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N")[..8];
            try
            {
                using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    gzip.CopyTo(output);
                try
                {
                    File.Move(temp, target, overwrite: true);
                }
                catch (IOException) when (File.Exists(target))
                {
                    // Another concurrent process extracted the same file first.
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        try { File.WriteAllText(ready, string.Empty); } catch (IOException) { }
    }

    private static string CacheRoot()
    {
        string baseDir;
        if (OperatingSystem.IsMacOS())
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches");
        else if (OperatingSystem.IsLinux())
            baseDir = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        else
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (string.IsNullOrWhiteSpace(baseDir)) baseDir = Path.GetTempPath();
        return Path.Combine(baseDir, "ChameLab", "native");
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        lock (Gate) RegisterAssembly(args.LoadedAssembly);
    }

    private static void RegisterAllLoadedAssemblies()
    {
        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            RegisterAssembly(loaded);
    }

    private static void RegisterAssembly(Assembly assembly)
    {
        if (!RegisteredAssemblies.Add(assembly)) return;
        try { NativeLibrary.SetDllImportResolver(assembly, Resolve); }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var map = _libraries;
        if (map is null) return IntPtr.Zero;

        var key = Path.GetFileName(libraryName);
        if (map.TryGetValue(key, out var path) && NativeLibrary.TryLoad(path, out var handle))
            return handle;

        var stem = Path.GetFileNameWithoutExtension(key);
        if (map.TryGetValue(stem, out path) && NativeLibrary.TryLoad(path, out handle))
            return handle;

        return IntPtr.Zero;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectoryW(string lpPathName);
}
