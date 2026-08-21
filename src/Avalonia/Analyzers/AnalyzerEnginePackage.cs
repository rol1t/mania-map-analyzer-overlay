using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

/// <summary>
/// A discovered analyzer-engine package and its validated, package-contained
/// resources. This type intentionally exposes no executable engine instance.
/// </summary>
public sealed class AnalyzerEnginePackage
{
    internal AnalyzerEnginePackage(
        string packageDirectory,
        string manifestPath,
        AnalyzerEngineManifest? manifest,
        string? runtimePath,
        string? workerPath,
        AnalyzerEngineAvailability availability)
    {
        PackageDirectory = packageDirectory;
        ManifestPath = manifestPath;
        Manifest = manifest;
        RuntimePath = runtimePath;
        WorkerPath = workerPath;
        Availability = availability;
    }

    public string PackageDirectory
    {
        get;
    }

    /// <summary>Alias used by hosts that treat all packages as source directories.</summary>
    public string SourceDirectory => PackageDirectory;

    public string ManifestPath
    {
        get;
    }

    public AnalyzerEngineManifest? Manifest
    {
        get;
    }

    public string? RuntimePath
    {
        get;
    }

    public string? WorkerPath
    {
        get;
    }

    /// <summary>Alias for the package runtime resource path.</summary>
    public string? RuntimeScriptPath => RuntimePath;

    /// <summary>Alias for the package worker resource path.</summary>
    public string? WorkerScriptPath => WorkerPath;

    public AnalyzerEngineAvailability Availability
    {
        get;
    }

    public IReadOnlyList<AnalyzerEngineDiagnostic> Diagnostics => Availability.Diagnostics;

    public bool IsAvailable => Availability.IsAvailable;

    public string? Id => Manifest?.Id?.Trim();

    public string? Name => string.IsNullOrWhiteSpace(Manifest?.Name)
        ? Id
        : Manifest?.Name?.Trim();

    public string? Version => Manifest?.Version?.Trim();

    public string? Protocol => Manifest?.Protocol?.Trim();

    public string? ResolveContainedFile(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return AnalyzerEngineCatalog.ResolveContainedFile(PackageDirectory, relativePath, requireExistingFile: true);
    }

    public string ReadRuntimeScript()
    {
        if (!IsAvailable || RuntimePath is null)
        {
            throw new InvalidOperationException(
                $"Analyzer engine '{Id ?? PackageDirectory}' is unavailable.");
        }

        return File.ReadAllText(RuntimePath);
    }

    public string ReadWorkerScript()
    {
        if (!IsAvailable || WorkerPath is null)
        {
            throw new InvalidOperationException(
                $"Analyzer engine '{Id ?? PackageDirectory}' is unavailable.");
        }

        return File.ReadAllText(WorkerPath);
    }

    internal AnalyzerEnginePackage AddDiagnostic(AnalyzerEngineDiagnostic diagnostic)
    {
        var diagnostics = Diagnostics.Concat([diagnostic]).ToArray();
        var status = Availability.Status switch
        {
            AnalyzerEngineAvailabilityStatus.Missing => AnalyzerEngineAvailabilityStatus.Missing,
            AnalyzerEngineAvailabilityStatus.Unavailable => AnalyzerEngineAvailabilityStatus.Unavailable,
            _ => AnalyzerEngineAvailabilityStatus.Invalid
        };
        return new AnalyzerEnginePackage(
            PackageDirectory,
            ManifestPath,
            Manifest,
            RuntimePath,
            WorkerPath,
            new AnalyzerEngineAvailability(status, diagnostics));
    }
}
