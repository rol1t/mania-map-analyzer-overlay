using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class PackageIntegrityTests
{
    [Fact]
    public void RejectsManifestWithMissingRequiredFieldsAndReportsIntegrityWarning()
    {
        var root = Path.Combine(Path.GetTempPath(), "PackageIntegrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var packageDir = Path.Combine(root, "bad-package");
            Directory.CreateDirectory(packageDir);
            File.WriteAllText(Path.Combine(packageDir, "manifest.json"), """{"manifestSchemaVersion":1,"kind":"analyzer-engine","id":"","version":"","protocol":"","protocolVersion":0,"runtime":"","worker":""}""");
            File.WriteAllText(Path.Combine(packageDir, "runtime.mjs"), "runtime");
            File.WriteAllText(Path.Combine(packageDir, "worker.mjs"), "worker");

            var sink = new RecordingSink();
            var catalog = new AnalyzerEngineCatalog(root, sink);
            var package = Assert.Single(catalog.List());
            Assert.False(package.IsAvailable);
            Assert.Contains(package.Diagnostics, diagnostic => diagnostic.Code == "engine.id_missing");
            Assert.Contains(sink.Entries, entry => entry.Code == "engine.id_missing");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void CatalogReportsUntrustedResourcePathEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "PackageIntegrityPath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var packageDir = Path.Combine(root, "escape");
            Directory.CreateDirectory(packageDir);
            var manifest = new
            {
                manifestSchemaVersion = 1,
                kind = "analyzer-engine",
                id = "escape-test",
                name = "Escape",
                version = "1.0.0",
                protocol = "test.escape",
                protocolVersion = 1,
                runtime = "../outside.mjs",
                worker = "worker.mjs",
                upstream = new
                {
                    name = "Test",
                    repository = "https://example.test",
                    license = "MIT",
                    integration = "dynamic-import",
                    supportedVersions = new[] { "1.0.0" }
                },
                capabilities = new
                {
                    algorithms = new[] { "Mixed" },
                    semanticMetricIds = new[] { "difficulty.star" }
                }
            };
            File.WriteAllText(Path.Combine(packageDir, "manifest.json"), JsonSerializer.Serialize(manifest));
            File.WriteAllText(Path.Combine(packageDir, "worker.mjs"), "worker");

            var sink = new RecordingSink();
            var catalog = new AnalyzerEngineCatalog(root, sink);
            var package = Assert.Single(catalog.List());
            Assert.False(package.IsAvailable);
            Assert.Contains(package.Diagnostics, diagnostic => diagnostic.Code == "engine.runtime_path_invalid");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class RecordingSink : IAnalyzerEngineDiagnosticSink
    {
        public List<AnalyzerEngineDiagnostic> Entries { get; } = [];
        public void Report(string operation, AnalyzerEngineDiagnostic diagnostic, Exception? exception = null) => Entries.Add(diagnostic);
    }
}
