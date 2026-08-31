using ManiaMapAnalyzerOverlay.Avalonia.Services;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class RuntimeAssetDeploymentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ManiaMapAnalyzerOverlay.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void EmbeddedPayloadDeploysAtomicallyAndIsReusable()
    {
        var assembly = typeof(RuntimeAssetDeployment).Assembly;

        var first = RuntimeAssetDeployment.DeployEmbeddedRuntimeAssets(assembly, _root);
        var second = RuntimeAssetDeployment.DeployEmbeddedRuntimeAssets(assembly, _root);

        Assert.Equal(first, second);
        Assert.True(File.Exists(Path.Combine(first, ".ready")));
        RuntimeAssetDeployment.ValidatePayload(first);
        Assert.True(File.Exists(Path.Combine(
            first,
            "Assets",
            "overlay",
            "presets",
            "companella",
            "manifest.json")));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".deploy-*"));
    }

    [Fact]
    public async Task ConcurrentDeploymentConvergesOnOneImmutableDirectory()
    {
        var assembly = typeof(RuntimeAssetDeployment).Assembly;

        var deployments = await Task.WhenAll(
            Task.Run(() => RuntimeAssetDeployment.DeployEmbeddedRuntimeAssets(assembly, _root)),
            Task.Run(() => RuntimeAssetDeployment.DeployEmbeddedRuntimeAssets(assembly, _root)));

        Assert.Equal(deployments[0], deployments[1]);
        Assert.Single(Directory.EnumerateDirectories(_root));
        RuntimeAssetDeployment.ValidatePayload(deployments[0]);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
