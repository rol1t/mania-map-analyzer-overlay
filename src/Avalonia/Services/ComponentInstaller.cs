using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

public interface IComponentArchiveDownloader
{
    Task DownloadAndVerifyAsync(
        GitHubAsset asset,
        string destination,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Installs verified Tosu and ManiaMapAnalyser archives into one component
/// root. The updater workflow decides when to call this boundary; this class
/// owns staging, replacement and rollback mechanics only.
/// </summary>
public sealed class ComponentInstaller
{
    private readonly string _tosuDirectory;
    private readonly string _tosuExecutableName;
    private readonly IComponentArchiveDownloader _downloader;
    private readonly Func<string, CancellationToken, Task> _stopOwnedProcess;

    public ComponentInstaller(
        string tosuDirectory,
        string tosuExecutableName,
        IComponentArchiveDownloader downloader,
        Func<string, CancellationToken, Task>? stopOwnedProcess = null)
    {
        _tosuDirectory = string.IsNullOrWhiteSpace(tosuDirectory)
            ? throw new ArgumentException("A Tosu component directory is required.", nameof(tosuDirectory))
            : tosuDirectory;
        _tosuExecutableName = string.IsNullOrWhiteSpace(tosuExecutableName)
            ? throw new ArgumentException("A Tosu executable name is required.", nameof(tosuExecutableName))
            : tosuExecutableName;
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _stopOwnedProcess = stopOwnedProcess ?? StopOwnedProcessAsync;
    }

    public async Task InstallTosuAsync(
        GitHubAsset asset,
        string temporaryRoot,
        IProgress<int>? downloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        string archivePath = Path.Combine(temporaryRoot, "tosu.zip");
        string extractPath = Path.Combine(temporaryRoot, "tosu-extract");
        await _downloader.DownloadAndVerifyAsync(
            asset,
            archivePath,
            progress: downloadProgress,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, extractPath, overwriteFiles: true);
        string[] files = Directory.EnumerateFiles(extractPath, _tosuExecutableName, SearchOption.AllDirectories).ToArray();
        if (files.Length != 1)
        {
            throw new InvalidOperationException($"The tosu archive does not contain a single {_tosuExecutableName} executable.");
        }

        Directory.CreateDirectory(_tosuDirectory);
        string target = Path.Combine(_tosuDirectory, _tosuExecutableName);
        await _stopOwnedProcess(target, cancellationToken).ConfigureAwait(false);
        string staged = target + ".new";
        string previous = target + ".previous";
        TryDeleteFile(staged);
        TryDeleteFile(previous);
        File.Copy(files[0], staged, overwrite: true);
        try
        {
            if (File.Exists(target))
            {
                File.Move(target, previous, overwrite: true);
            }

            File.Move(staged, target, overwrite: true);
            TryDeleteFile(previous);
            MakeExecutableIfNeeded(target);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Installing tosu executable", exception);
            if (!File.Exists(target) && File.Exists(previous))
            {
                File.Move(previous, target, overwrite: true);
            }

            TryDeleteFile(staged);
            throw;
        }
    }

    public async Task InstallAddonAsync(
        GitHubAsset asset,
        string temporaryRoot,
        IProgress<int>? downloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        string archivePath = Path.Combine(temporaryRoot, "addon.zip");
        string extractPath = Path.Combine(temporaryRoot, "addon-extract");
        await _downloader.DownloadAndVerifyAsync(
            asset,
            archivePath,
            progress: downloadProgress,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, extractPath, overwriteFiles: true);

        string[] metadataFiles = Directory.EnumerateFiles(extractPath, "metadata.txt", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("Name: ManiaMapAnalyser", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (metadataFiles.Length != 1)
        {
            throw new InvalidOperationException("The addon archive does not contain a single ManiaMapAnalyser root.");
        }

        string sourceRoot = Path.GetDirectoryName(metadataFiles[0])!;
        string staticRoot = Path.Combine(_tosuDirectory, "static");
        string target = Path.Combine(staticRoot, "ManiaMapAnalyser");
        string staged = Path.Combine(staticRoot, "ManiaMapAnalyser.new");
        string backupRoot = Path.Combine(_tosuDirectory, ".update-backup");
        string backup = Path.Combine(backupRoot, "ManiaMapAnalyser-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

        Directory.CreateDirectory(staticRoot);
        TryDeleteDirectory(staged);
        CopyDirectory(sourceRoot, staged);
        if (Directory.Exists(target))
        {
            Directory.CreateDirectory(backupRoot);
            Directory.Move(target, backup);
        }

        try
        {
            Directory.Move(staged, target);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Installing ManiaMapAnalyser", exception);
            if (Directory.Exists(backup) && !Directory.Exists(target))
            {
                Directory.Move(backup, target);
            }

            TryDeleteDirectory(staged);
            throw;
        }
    }

    private async Task StopOwnedProcessAsync(string executablePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(executablePath))
        {
            return;
        }

        string expected = Path.GetFullPath(executablePath);
        foreach (Process candidate in Process.GetProcessesByName("tosu"))
        {
            try
            {
                string? path = candidate.MainModule?.FileName;
                if (path is null || !string.Equals(
                        Path.GetFullPath(path),
                        expected,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    continue;
                }

                if (!candidate.HasExited)
                {
                    candidate.Kill(entireProcessTree: true);
                }

                await candidate.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                AppLogger.Warning("Stopping owned tosu process", "The process exited before it could be stopped.", exception);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                AppLogger.Warning("Stopping owned tosu process", "The process could not be inspected or stopped.", exception);
            }
            finally
            {
                candidate.Dispose();
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    private static void MakeExecutableIfNeeded(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"Deleting file '{path}'", "Cleanup failed.", exception);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning($"Deleting directory '{path}'", "Cleanup failed.", exception);
        }
    }
}
