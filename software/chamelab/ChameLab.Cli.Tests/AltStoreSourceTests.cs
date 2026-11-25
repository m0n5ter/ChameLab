using System.Text.Json;
using ChameLab.Tools;
using Xunit;

namespace ChameLab.Cli.Tests;

public class AltStoreSourceTests
{
    [Fact]
    public void VersionParsingMatchesGuiWorkflowFormula()
    {
        Assert.True(AltStoreSourceGenerator.TryParseVersion("v1.8.61", out var stable));
        Assert.Equal("1.8.61", stable.ShortVersion);
        Assert.Equal("11086100", stable.BuildVersion);
        Assert.Equal("v1.8.61", stable.DisplayTag);

        Assert.True(AltStoreSourceGenerator.TryParseVersion("v1.8.61-dev.3", out var dev));
        Assert.Equal("1.8.61", dev.ShortVersion);
        Assert.Equal("11086103", dev.BuildVersion);
        Assert.Equal("v1.8.61-dev.3", dev.DisplayTag);

        Assert.True(AltStoreSourceGenerator.TryParseVersion("v2.0.1-dev.120", out var v2));
        Assert.Equal("2.0.1", v2.ShortVersion);
        Assert.Equal("2000199", v2.BuildVersion);
    }

    [Fact]
    public void ReadsIosPlistPermissionsAndBundleId()
    {
        var root = FindRepoRoot();
        var meta = AltStoreSourceGenerator.ReadAppleMetadata(
            Path.Combine(root, "software/chamelab/ChameLab.iOS/Info.plist"),
            Path.Combine(root, "software/chamelab/ChameLab.iOS/Entitlements.plist"));

        Assert.Equal("io.github.m0n5ter.chamelab", meta.BundleIdentifier);
        Assert.Equal("13.0", meta.MinimumOsVersion);
        Assert.Empty(meta.Entitlements);
        Assert.Contains("NSBluetoothAlwaysUsageDescription", meta.Privacy.Keys);
        Assert.Contains("NSCameraUsageDescription", meta.Privacy.Keys);
        Assert.Contains("NSLocalNetworkUsageDescription", meta.Privacy.Keys);
    }

    [Fact]
    public void BuildsStableAndDevSourcesWithoutDuplicateBuilds()
    {
        var root = FindRepoRoot();
        var meta = AltStoreSourceGenerator.ReadAppleMetadata(
            Path.Combine(root, "software/chamelab/ChameLab.iOS/Info.plist"),
            Path.Combine(root, "software/chamelab/ChameLab.iOS/Entitlements.plist"));

        string[] changelog =
        [
            "## [unreleased][unreleased]",
            " - ChameLab: upcoming fix",
            "## [v1.8.61][2026-11-25] · firmware v2.4.27-cl",
            "### Summary",
            " - ChameLab: **Slot toggle** — enable or disable slots",
        ];

        const string inputJson = """
            {
              "releases": [
                {
                  "tag_name": "v1.8.61",
                  "name": "ChameLab v1.8.61",
                  "prerelease": false,
                  "draft": false,
                  "published_at": "2026-11-25T20:08:02Z",
                  "body": "Release notes",
                  "assets": [
                    {
                      "name": "chamelab-1.8.61-ios.ipa",
                      "size": 41638641,
                      "digest": "sha256:98f24677acb6e87432cdb83197a8a0abf221ff303cbe75e5f0e95a7edec1629b",
                      "browser_download_url": "https://example.test/v1.8.61/chamelab-1.8.61-ios.ipa"
                    }
                  ]
                },
                {
                  "tag_name": "v1.8.60",
                  "name": "ChameLab v1.8.60",
                  "prerelease": false,
                  "draft": false,
                  "published_at": "2026-11-24T11:25:13Z",
                  "body": "SHA-256:\n```\n81c3dedcba906edf76265ec44c2dcb2a92d22dafc943d7d0e0f7dadd0e3f68a5  chamelab-1.8.60-ios.ipa\n```",
                  "assets": [
                    {
                      "name": "chamelab-1.8.60-ios.ipa",
                      "size": 41617623,
                      "browser_download_url": "https://example.test/v1.8.60/chamelab-1.8.60-ios.ipa"
                    }
                  ]
                }
              ],
              "dev": {
                "tag_name": "dev",
                "name": "ChameLab v1.8.61-dev.2 (development)",
                "prerelease": true,
                "draft": false,
                "published_at": "2026-11-26T00:00:00Z",
                "body": "",
                "assets": [
                  {
                    "name": "chamelab-1.8.61-dev.2-ios.ipa",
                    "size": 41640000,
                    "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "browser_download_url": "https://example.test/dev/chamelab-1.8.61-dev.2-ios.ipa"
                  }
                ]
              }
            }
            """;

        using var doc = JsonDocument.Parse(inputJson);
        var (releases, dev) = AltStoreSourceGenerator.ParseInput(doc.RootElement);

        var stableSource = AltStoreSourceGenerator.BuildSource(releases, null, changelog, meta, "https://m0n5ter.github.io/ChameLab/", isDevFeed: false);
        var stableVersions = stableSource["apps"]![0]!["versions"]!.AsArray();
        Assert.Equal(2, stableVersions.Count);
        Assert.Equal("1.8.61", stableVersions[0]!["version"]!.GetValue<string>());
        Assert.Equal("11086100", stableVersions[0]!["buildVersion"]!.GetValue<string>());
        Assert.Equal("98f24677acb6e87432cdb83197a8a0abf221ff303cbe75e5f0e95a7edec1629b", stableVersions[0]!["sha256"]!.GetValue<string>());
        Assert.Equal("81c3dedcba906edf76265ec44c2dcb2a92d22dafc943d7d0e0f7dadd0e3f68a5", stableVersions[1]!["sha256"]!.GetValue<string>());
        Assert.Contains("Firmware v2.4.27-cl", stableVersions[0]!["localizedDescription"]!.GetValue<string>());

        var devSource = AltStoreSourceGenerator.BuildSource(releases, dev, changelog, meta, "https://m0n5ter.github.io/ChameLab/", isDevFeed: true);
        Assert.Equal("https://m0n5ter.github.io/ChameLab/altstore-dev.json", devSource["sourceURL"]!.GetValue<string>());
        var devVersions = devSource["apps"]![0]!["versions"]!.AsArray();
        Assert.Equal(3, devVersions.Count);
        Assert.Equal("1.8.61", devVersions[0]!["version"]!.GetValue<string>());
        Assert.Equal("11086102", devVersions[0]!["buildVersion"]!.GetValue<string>());
        Assert.Contains("upcoming fix", devVersions[0]!["localizedDescription"]!.GetValue<string>());

        // When dev has 0 commits after v1.8.61, the stable entry wins for (1.8.61, 11086100) without duplicating.
        using var sameCommitDoc = JsonDocument.Parse(inputJson.Replace("1.8.61-dev.2", "1.8.61"));
        var (sameReleases, sameDev) = AltStoreSourceGenerator.ParseInput(sameCommitDoc.RootElement);
        var deduped = AltStoreSourceGenerator.BuildSource(sameReleases, sameDev, changelog, meta, "https://m0n5ter.github.io/ChameLab/", isDevFeed: true);
        var dedupedVersions = deduped["apps"]![0]!["versions"]!.AsArray();
        Assert.Equal(2, dedupedVersions.Count);
        Assert.Equal("https://example.test/v1.8.61/chamelab-1.8.61-ios.ipa", dedupedVersions[0]!["downloadURL"]!.GetValue<string>());
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "software/chamelab/ChameLab.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("Repo root not found.");
    }
}
