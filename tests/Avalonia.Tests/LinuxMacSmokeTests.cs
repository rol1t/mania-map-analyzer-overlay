using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class LinuxMacSmokeTests
{
    [Fact]
    public void DocumentationServiceLoadsOnNonWindows()
    {
        var service = new DocumentationService();
        Assert.NotEmpty(service.Entries);
        var content = service.LoadContent("overview");
        Assert.False(string.IsNullOrWhiteSpace(content));
    }

    [Fact]
    public void AnalyzerEngineCatalogDoesNotRequireWindows()
    {
        var catalog = new AnalyzerEngineCatalog();
        // Catalog discovery is host-neutral; it may be empty on CI but must not throw.
        var packages = catalog.List();
        Assert.NotNull(packages);
    }

    [Fact]
    public void ScriptBridgeCanBeCreatedWithDelegateHostOffscreen()
    {
        // Simulates the Linux WPE headless host where WebView is offscreen.
        var catalog = new AnalyzerEngineCatalog();
        if (catalog.Available().Count == 0)
        {
            return;
        }

        var package = catalog.Available()[0];
        var host = new DelegateAnalyzerScriptHost((_, _) => Task.FromResult<string?>(null));
        var bridge = new AnalyzerEngineScriptBridge(package, host);
        Assert.Equal(package.Id, bridge.Descriptor.Id);
    }
}
