using ManiaMapAnalyzerOverlay.Avalonia.Services;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class AddonReleaseAssetTests
{
    [Theory]
    [InlineData("v2.0.0", "ManiaMapAnalyser.by.Leo_Black.zip")]
    [InlineData("v3.0.0", "ManiaMapAnalyser-by-Leo_Black-v3.0.0.zip")]
    [InlineData("3.0.0", "ManiaMapAnalyser-by-Leo_Black-v3.0.0.zip")]
    [InlineData("v3.1.0-beta.1+build.2", "ManiaMapAnalyser-by-Leo_Black-v3.1.0-beta.1+build.2.zip")]
    public void SelectsOnlyPluginArchiveAndPreservesItsDownloadMetadata(string tag, string name)
    {
        var plugin = new GitHubAsset
        {
            Name = name,
            DownloadUrl = "https://example.test/plugin.zip",
            Digest = "sha256:verified-by-downloader"
        };
        var release = new GitHubRelease
        {
            TagName = tag,
            Assets =
            [
                new() { Name = "bridges.zip" },
                new() { Name = "ManiaMapAnalyser-by-Leo_Black-v3.0.0-with-shell.zip" },
                new() { Name = "ManiaMapAnalyser-by-Leo_Black-v3.0.0-with-shell-linux.tar.gz" },
                plugin
            ]
        };

        Assert.Same(plugin, UpdateService.FindAddonAsset(release));
    }

    [Theory]
    [InlineData("ManiaMapAnalyser-by-Leo_Black-v3.0.0-with-shell.zip")]
    [InlineData("ManiaMapAnalyser-by-Leo_Black-v3.0.0.zip.sha256")]
    [InlineData("ManiaMapAnalyser-by-Leo_Black-v3.0.1.zip")]
    [InlineData("ManiaMapAnalyser-by-Leo_Black-v3x0x0.zip")]
    [InlineData("ManiaMapAnalyser-by-Leo_Black-v3.0.0-extra.zip")]
    [InlineData("bridges.zip")]
    public void RejectsUnrelatedOrMismatchedArchive(string name)
    {
        var release = new GitHubRelease { TagName = "v3.0.0", Assets = [new() { Name = name }] };

        Assert.Null(UpdateService.FindAddonAsset(release));
    }

    [Fact]
    public void RejectsAmbiguousPluginArchives()
    {
        var release = new GitHubRelease
        {
            TagName = "v3.0.0",
            Assets =
            [
                new() { Name = "ManiaMapAnalyser.by.Leo_Black.zip" },
                new() { Name = "ManiaMapAnalyser-by-Leo_Black-v3.0.0.zip" }
            ]
        };

        Assert.Null(UpdateService.FindAddonAsset(release));
    }
}
