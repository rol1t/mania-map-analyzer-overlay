using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class AnalyzerEngineCatalogTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "ManiaMapAnalyzerOverlay-AnalyzerEngineTests-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingDiagnosticSink _diagnosticSink = new();
    private readonly List<string> _additionalCleanupDirectories = [];

    public AnalyzerEngineCatalogTests() => Directory.CreateDirectory(_rootDirectory);

    [Fact]
    public void DiscoversValidPackageAndItsContainedResources()
    {
        var packageDirectory = CreatePackage("valid");
        var catalog = new AnalyzerEngineCatalog(_rootDirectory, _diagnosticSink);
        var package = Assert.Single(catalog.List());

        Assert.True(package.IsAvailable);
        Assert.Empty(package.Diagnostics);
        Assert.Equal("mania-map-analyser-headless", package.Id);
        Assert.Equal(Path.Combine(packageDirectory, "runtime.mjs"), package.RuntimePath);
        Assert.Equal(Path.Combine(packageDirectory, "worker.mjs"), package.WorkerPath);
        Assert.Equal(["2.0.0"], package.Manifest!.Upstream!.SupportedVersions);
        Assert.Empty(catalog.Diagnostics);
    }

    [Fact]
    public void RejectsParentDirectoryResourcePath()
    {
        CreatePackage("parent-traversal", runtime: "../outside.mjs", writeRuntime: false);

        var package = Assert.Single(new AnalyzerEngineCatalog(_rootDirectory, _diagnosticSink).List());

        Assert.False(package.IsAvailable);
        Assert.Contains(package.Diagnostics, diagnostic => diagnostic.Code == "engine.runtime_path_invalid");
        Assert.Contains(_diagnosticSink.Entries, diagnostic => diagnostic.Code == "engine.runtime_path_invalid");
    }

    [Fact]
    public void RejectsRootedResourcePath()
    {
        var rootedPath = Path.Combine(Path.GetTempPath(), "outside-runtime.mjs");
        CreatePackage("rooted", runtime: rootedPath, writeRuntime: false);

        var package = Assert.Single(new AnalyzerEngineCatalog(_rootDirectory, _diagnosticSink).List());

        Assert.False(package.IsAvailable);
        Assert.Contains(package.Diagnostics, diagnostic => diagnostic.Code == "engine.runtime_path_invalid");
        Assert.Contains(_diagnosticSink.Entries, diagnostic => diagnostic.Code == "engine.runtime_path_invalid");
    }

    [Fact]
    public void ReportsMissingRuntimeAsStructuredDiagnostic()
    {
        CreatePackage("missing-runtime", runtime: "missing-runtime.mjs", writeRuntime: false);

        var package = Assert.Single(new AnalyzerEngineCatalog(_rootDirectory, _diagnosticSink).List());

        Assert.False(package.IsAvailable);
        Assert.Equal(AnalyzerEngineAvailabilityStatus.Missing, package.Availability.Status);
        Assert.Contains(package.Diagnostics, diagnostic => diagnostic.Code == "engine.runtime_unavailable");
        Assert.Contains(_diagnosticSink.Entries, diagnostic => diagnostic.Code == "engine.runtime_unavailable");
    }

    [Fact]
    public void RejectsDuplicateEngineIds()
    {
        CreatePackage("first", id: "duplicate");
        CreatePackage("second", id: "duplicate");

        var catalog = new AnalyzerEngineCatalog(_rootDirectory, _diagnosticSink);
        var packages = catalog.List();

        Assert.Equal(2, packages.Count);
        Assert.All(packages, package => Assert.False(package.IsAvailable));
        Assert.Equal(2, catalog.Diagnostics.Count(diagnostic => diagnostic.Code == "engine.duplicate_id"));
        Assert.Equal(2, _diagnosticSink.Entries.Count(diagnostic => diagnostic.Code == "engine.duplicate_id"));
    }

    [Fact]
    public void RejectsIntermediateDirectorySymlinkEscapeWhenSupported()
    {
        var outsideDirectory = Path.Combine(
            Path.GetDirectoryName(_rootDirectory)!,
            Path.GetFileName(_rootDirectory) + "-outside");
        _additionalCleanupDirectories.Add(outsideDirectory);
        Directory.CreateDirectory(outsideDirectory);
        File.WriteAllText(Path.Combine(outsideDirectory, "runtime.mjs"), "outside runtime");

        var packageDirectory = CreatePackage(
            "symlink-escape",
            runtime: "linked/runtime.mjs",
            writeRuntime: false);
        var linkDirectory = Path.Combine(packageDirectory, "linked");
        try
        {
            Directory.CreateSymbolicLink(linkDirectory, outsideDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           PlatformNotSupportedException or NotSupportedException)
        {
            return;
        }

        var package = Assert.Single(new AnalyzerEngineCatalog(_rootDirectory, _diagnosticSink).List());

        Assert.False(package.IsAvailable);
        Assert.Contains(package.Diagnostics, diagnostic => diagnostic.Code == "engine.runtime_unavailable");
        Assert.Contains(_diagnosticSink.Entries, diagnostic => diagnostic.Code == "engine.runtime_unavailable");
    }

    [Fact]
    public void RejectsPackageDirectorySymlinkEscapeWhenSupported()
    {
        var outsideDirectory = Path.Combine(
            Path.GetDirectoryName(_rootDirectory)!,
            Path.GetFileName(_rootDirectory) + "-outside");
        _additionalCleanupDirectories.Add(outsideDirectory);
        var outsidePackageDirectory = CreatePackageInDirectory(outsideDirectory, "external-package");
        var packageLink = Path.Combine(_rootDirectory, "external-package-link");
        try
        {
            Directory.CreateSymbolicLink(packageLink, outsidePackageDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           PlatformNotSupportedException or NotSupportedException)
        {
            return;
        }

        var package = Assert.Single(new AnalyzerEngineCatalog(_rootDirectory, _diagnosticSink).List());

        Assert.False(package.IsAvailable);
        Assert.Equal(AnalyzerEngineAvailabilityStatus.Unavailable, package.Availability.Status);
        Assert.Contains(package.Diagnostics, diagnostic => diagnostic.Code == "engine.package_directory_escape");
        Assert.Contains(_diagnosticSink.Entries, diagnostic => diagnostic.Code == "engine.package_directory_escape");
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }

        foreach (var directory in _additionalCleanupDirectories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private string CreatePackage(
        string directoryName,
        string id = "mania-map-analyser-headless",
        string runtime = "runtime.mjs",
        string worker = "worker.mjs",
        bool writeRuntime = true)
    {
        return CreatePackageInDirectory(_rootDirectory, directoryName, id, runtime, worker, writeRuntime);
    }

    private static string CreatePackageInDirectory(
        string parentDirectory,
        string directoryName,
        string id = "mania-map-analyser-headless",
        string runtime = "runtime.mjs",
        string worker = "worker.mjs",
        bool writeRuntime = true)
    {
        var packageDirectory = Path.Combine(parentDirectory, directoryName);
        Directory.CreateDirectory(packageDirectory);
        if (writeRuntime)
        {
            var runtimeDirectory = Path.GetDirectoryName(Path.Combine(packageDirectory, runtime));
            if (!string.IsNullOrWhiteSpace(runtimeDirectory))
            {
                Directory.CreateDirectory(runtimeDirectory);
            }

            File.WriteAllText(Path.Combine(packageDirectory, runtime), "runtime");
        }

        File.WriteAllText(Path.Combine(packageDirectory, worker), "worker");
        File.WriteAllText(
            Path.Combine(packageDirectory, "manifest.json"),
            CreateManifest(id, runtime, worker));
        return packageDirectory;
    }

    private static string CreateManifest(string id, string runtime, string worker) => $$"""
        {
          "manifestSchemaVersion": 1,
          "kind": "analyzer-engine",
          "id": {{JsonSerializer.Serialize(id)}},
          "name": "Test analyzer engine",
          "version": "1.0.0",
          "protocol": "test.headless",
          "protocolVersion": 1,
          "runtime": {{JsonSerializer.Serialize(runtime)}},
          "worker": {{JsonSerializer.Serialize(worker)}},
          "upstream": {
            "name": "Test upstream",
            "repository": "https://example.test/upstream",
            "license": "MIT",
            "integration": "dynamic-import",
            "supportedVersions": ["2.0.0"]
          },
          "capabilities": {
            "algorithms": ["Sunny"],
            "optionalAlgorithms": {},
            "semanticMetricIds": ["difficulty.star"]
          }
        }
        """;

    private sealed class RecordingDiagnosticSink : IAnalyzerEngineDiagnosticSink
    {
        public List<AnalyzerEngineDiagnostic> Entries { get; } = [];

        public void Report(string operation, AnalyzerEngineDiagnostic diagnostic, Exception? exception = null) =>
            Entries.Add(diagnostic);
    }
}
