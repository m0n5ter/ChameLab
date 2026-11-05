using System.IO.Compression;
using Xunit;

namespace ChameLab.Cli.Tests;

public class PackagingTests
{
    [Fact]
    public async Task ReleaseNamesKeepVersionsAndArchiveContainsExecutable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chamelab-packaging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source"); Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "chamelab"), "executable fixture");
            var zip = Path.Combine(directory, "client-windows.zip");
            Assert.Equal(0, await ChameLab.Tools.Program.Main(["zip", source, zip]));
            using (var archive = ZipFile.OpenRead(zip))
            {
                Assert.Equal("chamelab", Assert.Single(archive.Entries).FullName);
                using var reader = new StreamReader(archive.Entries[0].Open());
                Assert.Equal("executable fixture", await reader.ReadToEndAsync());
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "ultra-dfu-app.zip"), "firmware fixture");
            Assert.Equal(0, await ChameLab.Tools.Program.Main(["name-release-files", directory, "1.3.33", "2.3.4-cl"]));
            Assert.True(File.Exists(Path.Combine(directory, "client-1.3.33-windows.zip")));
            Assert.True(File.Exists(Path.Combine(directory, "ultra-dfu-app-2.3.4-cl.zip")));
            Assert.Equal(0, await ChameLab.Tools.Program.Main(["name-release-files", directory, "1.3.33", "2.3.4-cl"]));
            Assert.True(File.Exists(Path.Combine(directory, "client-1.3.33-windows.zip")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
