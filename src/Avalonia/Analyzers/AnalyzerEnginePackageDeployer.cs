using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

/// <summary>
/// Copies one validated analyzer package into the static directory served by
/// tosu. The copy is staged first and the final directory swap is kept as one
/// recoverable operation, so a failed copy cannot leave a half-installed
/// package visible to the WebView.
/// </summary>
public sealed class AnalyzerEnginePackageDeployer
{
    public const string StaticDirectoryName = "ManiaMapAnalyzerOverlay";
    public const string EnginesDirectoryName = "engines";

    private readonly IAnalyzerEngineDiagnosticSink _diagnosticSink;

    public AnalyzerEnginePackageDeployer(IAnalyzerEngineDiagnosticSink? diagnosticSink = null)
    {
        _diagnosticSink = diagnosticSink ?? new AppLoggerAnalyzerEngineDiagnosticSink();
    }

    public AnalyzerEnginePackageDeployment Deploy(
        AnalyzerEnginePackage package,
        string tosuDirectory)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(tosuDirectory);

        if (!package.IsAvailable || string.IsNullOrWhiteSpace(package.Id))
        {
            throw ReportFailure(
                "engine.deploy_unavailable",
                $"Analyzer engine package '{package.PackageDirectory}' is unavailable and cannot be deployed.",
                package.PackageDirectory,
                new InvalidDataException("The analyzer engine package is not available."));
        }

        var packageId = package.Id.Trim();
        EnsureSafePackageId(packageId);

        var tosuRoot = Path.GetFullPath(tosuDirectory);
        var staticRoot = Path.Combine(tosuRoot, "static", StaticDirectoryName);
        var enginesRoot = Path.Combine(staticRoot, EnginesDirectoryName);
        var targetDirectory = Path.Combine(enginesRoot, packageId);
        EnsureContained(staticRoot, targetDirectory);

        string? stagingDirectory = null;
        string? backupDirectory = null;
        var replacedExisting = Directory.Exists(targetDirectory);
        var movedExisting = false;
        try
        {
            EnsureSafeExistingTree(tosuRoot);
            Directory.CreateDirectory(enginesRoot);
            EnsureSafeExistingTree(staticRoot);
            EnsureSafeExistingTree(enginesRoot);

            if (File.Exists(targetDirectory) || IsReparsePoint(targetDirectory))
            {
                throw new IOException(
                    $"The analyzer engine target '{targetDirectory}' is not a regular directory.");
            }

            stagingDirectory = targetDirectory + ".staging-" + Guid.NewGuid().ToString("N");
            EnsureContained(enginesRoot, stagingDirectory);
            CopyPackage(package, stagingDirectory);

            if (Directory.Exists(targetDirectory))
            {
                backupDirectory = targetDirectory + ".backup-" + Guid.NewGuid().ToString("N");
                EnsureContained(enginesRoot, backupDirectory);
                Directory.Move(targetDirectory, backupDirectory);
                movedExisting = true;
            }

            Directory.Move(stagingDirectory, targetDirectory);
            stagingDirectory = null;

            if (backupDirectory is not null)
            {
                TryDeleteDirectory(backupDirectory, "Removing previous analyzer engine package backup");
                backupDirectory = null;
            }

            return new AnalyzerEnginePackageDeployment(
                packageId,
                targetDirectory,
                replacedExisting);
        }
        catch (Exception exception)
        {
            var failure = ReportFailure(
                "Deploying analyzer engine package",
                $"Analyzer engine package '{packageId}' could not be deployed into '{targetDirectory}'.",
                targetDirectory,
                exception);

            try
            {
                if (stagingDirectory is not null && Directory.Exists(stagingDirectory))
                {
                    TryDeleteDirectory(stagingDirectory, "Cleaning failed analyzer engine staging directory");
                }

                if (movedExisting && backupDirectory is not null && !Directory.Exists(targetDirectory))
                {
                    Directory.Move(backupDirectory, targetDirectory);
                    backupDirectory = null;
                }
            }
            catch (Exception rollbackException)
            {
                _diagnosticSink.Report(
                    "Rolling back analyzer engine deployment",
                    new AnalyzerEngineDiagnostic(
                        "engine.deploy_rollback_failed",
                        $"The failed analyzer engine deployment could not be fully rolled back: {rollbackException.Message}",
                        targetDirectory,
                        AnalyzerEngineDiagnosticSeverity.Error,
                        rollbackException),
                    rollbackException);
            }

            throw failure;
        }
        finally
        {
            if (stagingDirectory is not null && Directory.Exists(stagingDirectory))
            {
                TryDeleteDirectory(stagingDirectory, "Cleaning analyzer engine staging directory");
            }

            if (backupDirectory is not null && Directory.Exists(backupDirectory))
            {
                TryDeleteDirectory(backupDirectory, "Cleaning analyzer engine backup directory");
            }
        }
    }

    private void CopyPackage(AnalyzerEnginePackage package, string destinationDirectory)
    {
        var sourceDirectory = Path.GetFullPath(package.PackageDirectory);
        EnsureSafeExistingTree(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);

        foreach (var sourcePath in Directory.EnumerateFileSystemEntries(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            if (IsReparsePoint(sourcePath))
            {
                throw new InvalidDataException(
                    $"The analyzer engine package contains a link or reparse point at '{sourcePath}'.");
            }

            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            EnsureRelativePath(relativePath);
            var destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, relativePath));
            EnsureContained(destinationDirectory, destinationPath);

            if (Directory.Exists(sourcePath))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            if (!File.Exists(sourcePath))
            {
                throw new IOException($"The analyzer engine package entry '{sourcePath}' disappeared during deployment.");
            }

            EnsureContained(sourceDirectory, sourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }

        if (!File.Exists(Path.Combine(destinationDirectory, "manifest.json")))
        {
            throw new InvalidDataException("The staged analyzer engine package is missing manifest.json.");
        }
    }

    private static void EnsureSafePackageId(string packageId)
    {
        if (packageId is "." or ".." ||
            packageId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':']) >= 0)
        {
            throw new ArgumentException("An analyzer engine id must be a single safe directory name.", nameof(packageId));
        }
    }

    private static void EnsureRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new InvalidDataException("The analyzer engine package contains an invalid relative path.");
        }

        var normalized = Path.GetFullPath(Path.Combine("package", path));
        if (!IsContained(Path.GetFullPath("package"), normalized))
        {
            throw new InvalidDataException("The analyzer engine package contains a path traversal entry.");
        }
    }

    private static void EnsureContained(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        if (!IsContained(fullRoot, fullCandidate))
        {
            throw new InvalidDataException(
                $"The analyzer engine path '{fullCandidate}' escapes '{fullRoot}'.");
        }
    }

    private static bool IsContained(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalizedRoot.Length == 0)
        {
            normalizedRoot = Path.GetPathRoot(root)!;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(normalizedRoot, candidate, comparison) ||
               candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static void EnsureSafeExistingTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        if (IsReparsePoint(path))
        {
            throw new IOException($"The analyzer engine deployment directory '{path}' is a link or reparse point.");
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private void TryDeleteDirectory(string path, string operation)
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
            _diagnosticSink.Report(
                operation,
                new AnalyzerEngineDiagnostic(
                    "engine.deploy_cleanup_failed",
                    $"Could not clean up analyzer engine deployment directory '{path}': {exception.Message}",
                    path,
                    AnalyzerEngineDiagnosticSeverity.Warning,
                    exception),
                exception);
        }
    }

    private AnalyzerEngineDeploymentException ReportFailure(
        string operation,
        string message,
        string path,
        Exception exception)
    {
        var diagnostic = new AnalyzerEngineDiagnostic(
            "engine.deploy_failed",
            message,
            path,
            AnalyzerEngineDiagnosticSeverity.Error,
            exception);
        _diagnosticSink.Report(operation, diagnostic, exception);
        return new AnalyzerEngineDeploymentException(message, exception, diagnostic);
    }
}

public sealed class AnalyzerEnginePackageDeployment
{
    internal AnalyzerEnginePackageDeployment(string packageId, string targetDirectory, bool replacedExisting)
    {
        PackageId = packageId;
        TargetDirectory = targetDirectory;
        ReplacedExisting = replacedExisting;
    }

    public string PackageId
    {
        get;
    }

    public string TargetDirectory
    {
        get;
    }

    public bool ReplacedExisting
    {
        get;
    }
}

public sealed class AnalyzerEngineDeploymentException : IOException
{
    public AnalyzerEngineDeploymentException(string message, Exception innerException, AnalyzerEngineDiagnostic diagnostic)
        : base(message, innerException)
    {
        Diagnostic = diagnostic;
    }

    public AnalyzerEngineDiagnostic Diagnostic
    {
        get;
    }
}

internal sealed class AppLoggerAnalyzerEngineDiagnosticSink : IAnalyzerEngineDiagnosticSink
{
    public void Report(string operation, AnalyzerEngineDiagnostic diagnostic, Exception? exception = null)
    {
        ManiaMapAnalyzerOverlay.Avalonia.Services.AppLogger.Error(
            operation,
            exception ?? diagnostic.Exception ?? new InvalidDataException(diagnostic.Message));
    }
}
