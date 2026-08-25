using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class UpdateStateStoreTests
{
    [Fact]
    public async Task SavesAndLoadsStateFromInjectedPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "ManiaMapAnalyzerOverlay-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new UpdateStateStore(
                Path.Combine(root, "data", "install-state.json"),
                Path.Combine(root, "legacy", "install-state.json"),
                Path.Combine(root, "data"),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var expected = new InstallState
            {
                TosuVersion = "tosu-test",
                AddonVersion = "addon-test",
                Compatibility = "supported"
            };

            await store.SaveAsync(expected);
            InstallState actual = await store.LoadAsync();

            Assert.Equal(expected.TosuVersion, actual.TosuVersion);
            Assert.Equal(expected.AddonVersion, actual.AddonVersion);
            Assert.Equal(expected.Compatibility, actual.Compatibility);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "data"), "install-state.json.tmp-*"));
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
    public async Task FallsBackToLegacyPathWhenPrimaryIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "ManiaMapAnalyzerOverlay-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var primaryPath = Path.Combine(root, "data", "install-state.json");
            var legacyPath = Path.Combine(root, "legacy", "install-state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            await File.WriteAllTextAsync(
                legacyPath,
                "{\"tosuVersion\":\"legacy-tosu\",\"addonVersion\":\"legacy-addon\"}");
            var store = new UpdateStateStore(primaryPath, legacyPath, Path.Combine(root, "data"));

            InstallState actual = await store.LoadAsync();

            Assert.Equal("legacy-tosu", actual.TosuVersion);
            Assert.Equal("legacy-addon", actual.AddonVersion);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
