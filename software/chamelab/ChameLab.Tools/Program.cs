using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ChameLab.Tools;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["generate-hardnested"]: HardnestedTools.Generate(Root(), false); break;
                case ["generate-hardnested", "--check"]: HardnestedTools.Generate(Root(), true); break;
                case ["import-hardnested-tables", var upstream]: HardnestedTools.ImportTables(Root(), upstream); break;
                case ["prepare-hardnested-reference", var upstream]: await HardnestedTools.PrepareReference(upstream); break;
                case ["compare-hardnested", .. var rest]: await BenchmarkTools.Compare(rest); break;
                case ["reference-vectors", .. var rest]: await BenchmarkTools.ReferenceVectors(rest); break;
                case ["benchmark", var name, .. var rest]: await BenchmarkTools.Managed(name, rest); break;
                case ["pr-review", var mode]: await PrReviewCycle.FromEnvironment(mode); break;
                case ["read-mfc4k", .. var rest]: await Mfc4kReader.Run(rest); break;
                case ["check-languages"]: await CheckLanguages(); break;
                case ["zip", var source, var destination]: Archive(source, destination); break;
                case ["name-release-files", var folder, var appVersion, var firmwareVersion]: NameReleaseFiles(folder, appVersion, firmwareVersion); break;
                case ["--help"] or []:
                    Console.WriteLine("ChameLab development tools\n  generate-hardnested [--check]\n  import-hardnested-tables PROXMARK3_CHECKOUT\n  prepare-hardnested-reference DISPOSABLE_PROXMARK3_CHECKOUT\n  compare-hardnested MANAGED NATIVE OUTPUT [REPETITIONS] [--cases FILE] [--only CASE...]\n  reference-vectors --generator EXE --nested EXE --staticnested EXE --mfkey32v2 EXE [--output JSON]\n  benchmark nested|static-encrypted [ITERATIONS WARMUPS]\n  read-mfc4k --help\n  pr-review MODE\n  zip SOURCE_DIRECTORY OUTPUT.zip\n  name-release-files DIRECTORY APP_VERSION FIRMWARE_VERSION");
                    break;
                default: throw new ArgumentException("Unknown command. Run with --help.");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    internal static string Root()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "software/chamelab/ChameLab.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run this tool inside the ChameLab checkout.");
    }

    private static async Task CheckLanguages()
    {
        var root = Root();
        var files = (await Run("git", ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], root)).Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct();
        var otherLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".py", ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".go", ".rs", ".js", ".cjs", ".mjs", ".ts", ".tsx", ".jsx", ".sh", ".ps1", ".bat", ".cmd", ".java", ".kt", ".kts", ".rb", ".swift", ".m", ".mm", ".asm", ".s", ".lua", ".dart", ".fs", ".vb" };
        var invalid = files.Where(f => File.Exists(System.IO.Path.Combine(root, f)) && otherLanguages.Contains(System.IO.Path.GetExtension(f))
            && !f.StartsWith("firmware/", StringComparison.Ordinal) && !f.StartsWith("website/", StringComparison.Ordinal)
            && !f.StartsWith("software/chamelab/ChameLab.Browser/wwwroot/", StringComparison.Ordinal)
            && !f.StartsWith("software/chamelab/ChameLab.Browser/tests/", StringComparison.Ordinal)).ToArray();
        if (invalid.Length > 0) throw new InvalidDataException("Non-C# source outside firmware/web: " + string.Join(", ", invalid));
        Console.WriteLine("Source language audit passed: client/tools are C#; firmware and web are exempt.");
    }

    internal static async Task<string> Run(string executable, IEnumerable<string> args, string? directory = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (directory is not null) start.WorkingDirectory = directory;
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start " + executable);
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var text = await output; var errors = await error;
        if (process.ExitCode != 0) throw new IOException($"{executable} exited with {process.ExitCode}: {errors}\n{text}");
        return text;
    }

    private static void Archive(string source, string destination)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        // Collect before opening the archive; never accidentally include the output itself.
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        if (files.Any(f => string.Equals(f, destination, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Archive already exists within the source directory.");
        using var stream = new FileStream(destination, FileMode.CreateNew);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            var entry = archive.CreateEntry(Path.GetRelativePath(source, file).Replace('\\', '/'), CompressionLevel.SmallestSize);
            if (!OperatingSystem.IsWindows()) entry.ExternalAttributes = ((int)File.GetUnixFileMode(file) | 0x8000) << 16;
            using var output = entry.Open(); using var input = File.OpenRead(file); input.CopyTo(output);
        }
        Console.WriteLine($"Packaged {files.Length} files: {destination}");
    }

    private static void NameReleaseFiles(string folder, string app, string firmware)
    {
        foreach (var version in new[] { app, firmware })
            if (!Regex.IsMatch(version, @"\A[0-9A-Za-z][0-9A-Za-z.+-]*\z")) throw new ArgumentException("Invalid version: " + version);
        folder = Path.GetFullPath(folder);
        foreach (var file in Directory.GetFiles(folder).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file); string? next = null;
            if (name.StartsWith("chamelab-") || name.StartsWith("client-"))
            {
                var split = name.IndexOf('-') + 1;
                if (!name[split..].StartsWith(app + "-", StringComparison.Ordinal)) next = name[..split] + app + "-" + name[split..];
            }
            else if (name.Contains("-dfu-") || name.Contains("-binaries."))
            {
                var ext = Path.GetExtension(name); var stem = Path.GetFileNameWithoutExtension(name);
                if (!stem.EndsWith("-" + firmware, StringComparison.Ordinal)) next = stem + "-" + firmware + ext;
            }
            if (next is not null) { File.Move(file, Path.Combine(folder, next)); Console.WriteLine(next); }
        }
    }
}
