using System.IO.Compression;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class ComponentInstallerTests
{
    [Fact]
    public async Task InstallsTosuIntoInjectedRootWithStagedReplacement()
    {
        using var root = new TemporaryDirectory();
        string tosuRoot = Path.Combine(root.Path, "tosu");
        var downloader = new ArchiveDownloader((destination, _) =>
        {
            using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
            ZipArchiveEntry entry = archive.CreateEntry("release/tosu.exe");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("tosu-binary");
        });
        string? stoppedPath = null;
        var installer = new ComponentInstaller(
            tosuRoot,
            "tosu.exe",
            downloader,
            (path, _) =>
            {
                stoppedPath = path;
                return Task.CompletedTask;
            });

        await installer.InstallTosuAsync(
            new GitHubAsset { Name = "tosu.zip", DownloadUrl = "https://example.test/tosu.zip" },
            root.Path);

        Assert.Equal(Path.Combine(tosuRoot, "tosu.exe"), stoppedPath);
        Assert.Equal("tosu-binary", await File.ReadAllTextAsync(Path.Combine(tosuRoot, "tosu.exe")));
        Assert.False(File.Exists(Path.Combine(tosuRoot, "tosu.exe.new")));
    }

    [Fact]
    public async Task InstallsAddonUnderInjectedTosuStaticRoot()
    {
        using var root = new TemporaryDirectory();
        string tosuRoot = Path.Combine(root.Path, "tosu");
        var downloader = new ArchiveDownloader((destination, _) =>
        {
            using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
            ZipArchiveEntry metadata = archive.CreateEntry("package/metadata.txt");
            using (var writer = new StreamWriter(metadata.Open()))
            {
                writer.Write("Name: ManiaMapAnalyser\n");
            }

            ZipArchiveEntry asset = archive.CreateEntry("package/runtime.js");
            using var assetWriter = new StreamWriter(asset.Open());
            assetWriter.Write("runtime");
        });
        var installer = new ComponentInstaller(tosuRoot, "tosu.exe", downloader);

        await installer.InstallAddonAsync(
            new GitHubAsset { Name = "addon.zip", DownloadUrl = "https://example.test/addon.zip" },
            root.Path);

        string installedRoot = Path.Combine(tosuRoot, "static", "ManiaMapAnalyser");
        Assert.Equal("Name: ManiaMapAnalyser\n", await File.ReadAllTextAsync(Path.Combine(installedRoot, "metadata.txt")));
        Assert.Equal("runtime", await File.ReadAllTextAsync(Path.Combine(installedRoot, "runtime.js")));
    }

    private sealed class ArchiveDownloader : IComponentArchiveDownloader
    {
        private readonly Action<string, GitHubAsset> _writeArchive;

        public ArchiveDownloader(Action<string, GitHubAsset> writeArchive) => _writeArchive = writeArchive;

        public Task DownloadAndVerifyAsync(
            GitHubAsset asset,
            string destination,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            _writeArchive(destination, asset);
            progress?.Report(100);
            return Task.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ManiaMapAnalyzerOverlay-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path
        {
            get;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
