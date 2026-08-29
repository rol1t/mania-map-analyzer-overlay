using System;
using System.Collections.Immutable;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Avalonia.Models;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class HeadlessAnalysisKeyTests
{
    [Fact]
    public void SameBeatmapAndConfigAreEqual()
    {
        var snapshot = CreateSnapshot(mods: new[] { "HD", "HR" });
        var equivalentSnapshot = CreateSnapshot(mods: new[] { "HD", "HR" });
        var configuration = CreateConfiguration();

        var key1 = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(snapshot, configuration);
        var key2 = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(equivalentSnapshot, configuration);

        Assert.Equal(key1, key2);
        Assert.True(HeadlessAnalysisKeyBuilder.IsSameBeatmapAndConfig(equivalentSnapshot, configuration, key1, key1.SceneKey));
    }

    [Fact]
    public void ModsInDifferentOrderProduceEqualKeys()
    {
        var snapshotA = CreateSnapshot(mods: new[] { "HR", "HD" });
        var snapshotB = CreateSnapshot(mods: new[] { "HD", "HR" });
        var configuration = CreateConfiguration();

        var keyA = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(snapshotA, configuration);
        var keyB = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(snapshotB, configuration);

        Assert.Equal(keyA, keyB);
        Assert.Equal(keyA.BeatmapKey, keyB.BeatmapKey);
        Assert.Equal(keyA.SceneKey, keyB.SceneKey);
        Assert.Equal("HD,HR", keyA.BeatmapKey.Mods);
        Assert.Equal("HD,HR", keyA.SceneKey.Mods);
    }

    [Fact]
    public void DifferentRateAreNotEqual()
    {
        var snapshot = CreateSnapshot(rate: 1.0);
        var configuration = CreateConfiguration();
        var originalKey = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(snapshot, configuration);

        var fasterSnapshot = CreateSnapshot(rate: 1.5);
        var newKey = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(fasterSnapshot, configuration);

        Assert.NotEqual(originalKey, newKey);
        Assert.True(HeadlessAnalysisKeyBuilder.IsNewSceneGeneration(fasterSnapshot, configuration, originalKey.SceneKey));
    }

    [Fact]
    public void DifferentModsAreNotEqual()
    {
        var snapshot = CreateSnapshot(mods: ["HD"]);
        var configuration = CreateConfiguration();
        var originalKey = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(snapshot, configuration);

        var moddedSnapshot = CreateSnapshot(mods: ["DT"]);
        var newKey = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(moddedSnapshot, configuration);

        Assert.NotEqual(originalKey, newKey);
        Assert.True(HeadlessAnalysisKeyBuilder.IsNewSceneGeneration(moddedSnapshot, configuration, originalKey.SceneKey));
    }

    [Fact]
    public void RateAndModsChangeKeepsTheSameBeatmapRevision()
    {
        var configuration = CreateConfiguration();
        var original = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(
            CreateSnapshot(rate: 1.0, mods: ["NM"]),
            configuration);
        var modified = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(
            CreateSnapshot(rate: 1.5, mods: ["DT"]),
            configuration);

        Assert.True(HeadlessAnalysisKeyBuilder.IsSameBeatmapRevision(original, modified));
    }

    [Fact]
    public void DifferentBeatmapRevisionDoesNotBypassTransientGuard()
    {
        var configuration = CreateConfiguration();
        var original = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(
            CreateSnapshot(rawBeatmap: "osu!mania-a"),
            configuration);
        var different = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(
            CreateSnapshot(rawBeatmap: "osu!mania-bb"),
            configuration);

        Assert.False(HeadlessAnalysisKeyBuilder.IsSameBeatmapRevision(original, different));
    }

    [Fact]
    public void DifferentConfigurationAreNotEqual()
    {
        var snapshot = CreateSnapshot();
        var configuration = CreateConfiguration();
        var originalKey = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(snapshot, configuration);

        var differentConfiguration = CreateConfiguration(algorithm: "Different");
        var newKey = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(snapshot, differentConfiguration);

        Assert.NotEqual(originalKey, newKey);
        Assert.True(HeadlessAnalysisKeyBuilder.IsNewSceneGeneration(snapshot, differentConfiguration, originalKey.SceneKey));
    }

    [Fact]
    public void DifferentEffectiveOptionsProduceDifferentConfigurationIdentities()
    {
        var snapshot = CreateSnapshot();
        EffectiveAnalysisConfiguration first = CreateConfiguration() with
        {
            DefaultOptions = ImmutableDictionary<string, JsonElement>.Empty.Add(
                "strainWeight",
                JsonSerializer.SerializeToElement(1.0))
        };
        EffectiveAnalysisConfiguration second = CreateConfiguration() with
        {
            DefaultOptions = ImmutableDictionary<string, JsonElement>.Empty.Add(
                "strainWeight",
                JsonSerializer.SerializeToElement(2.0))
        };

        HeadlessAnalysisKey firstKey = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(snapshot, first.Normalize());
        HeadlessAnalysisKey secondKey = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(snapshot, second.Normalize());

        Assert.NotEqual(firstKey.SceneKey, secondKey.SceneKey);
        Assert.NotEqual(
            HeadlessAnalysisKeyBuilder.BuildConfigurationIdentity(firstKey),
            HeadlessAnalysisKeyBuilder.BuildConfigurationIdentity(secondKey));
    }

    [Fact]
    public void ConfigurationIdentityCanonicalizesNestedOptionPropertyOrder()
    {
        using JsonDocument firstDocument = JsonDocument.Parse("{\"weight\":2,\"enabled\":true}");
        using JsonDocument secondDocument = JsonDocument.Parse("{\"enabled\":true,\"weight\":2}");
        JsonElement firstValue = firstDocument.RootElement.Clone();
        JsonElement secondValue = secondDocument.RootElement.Clone();
        EffectiveAnalysisConfiguration first = CreateConfiguration() with
        {
            DefaultOptions = ImmutableDictionary<string, JsonElement>.Empty.Add("nested", firstValue)
        };
        EffectiveAnalysisConfiguration second = CreateConfiguration() with
        {
            DefaultOptions = ImmutableDictionary<string, JsonElement>.Empty.Add("nested", secondValue)
        };

        Assert.Equal(
            HeadlessAnalysisKeyBuilder.BuildConfigurationIdentity(first),
            HeadlessAnalysisKeyBuilder.BuildConfigurationIdentity(second));
    }

    [Fact]
    public void SceneKeyUsesTheNormalizedEffectiveConfiguration()
    {
        var snapshot = CreateSnapshot();
        var raw = new EffectiveAnalysisConfiguration();
        EffectiveAnalysisConfiguration normalized = raw.Normalize();

        Assert.Equal(
            HeadlessAnalysisKeyBuilder.BuildSceneKey(snapshot, normalized),
            HeadlessAnalysisKeyBuilder.BuildSceneKey(snapshot, raw));
    }

    [Fact]
    public void DifferentWidgetContentsProduceDifferentConfigurationIdentities()
    {
        EffectiveAnalysisConfiguration first = CreateConfiguration();
        EffectiveWidgetSpec originalWidget = first.Widgets[0];
        var changedWidget = new EffectiveWidgetSpec(
            "custom-overlay",
            originalWidget.Sources,
            originalWidget.Bindings);
        EffectiveAnalysisConfiguration second = first with
        {
            Widgets = [changedWidget]
        };

        Assert.NotEqual(
            HeadlessAnalysisKeyBuilder.BuildConfigurationIdentity(first),
            HeadlessAnalysisKeyBuilder.BuildConfigurationIdentity(second));
    }

    [Fact]
    public void DifferentRawBeatmapLength_ChangesAnalysisKeyButNotSceneKey()
    {
        var snapshot = CreateSnapshot(rawBeatmap: "osu file format v14\n[General]\nMode:3");
        var configuration = CreateConfiguration();
        var originalKey = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(snapshot, configuration);

        var longerSnapshot = CreateSnapshot(rawBeatmap: "osu file format v14\n[General]\nMode:3\n[HitObjects]\n1,2,3,4");
        var newKey = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(longerSnapshot, configuration);

        Assert.NotEqual(originalKey, newKey);
        Assert.Equal(originalKey.SceneKey, newKey.SceneKey);
        Assert.False(HeadlessAnalysisKeyBuilder.IsNewSceneGeneration(longerSnapshot, configuration, originalKey.SceneKey));
    }

    [Fact]
    public void NullPreviousKeys_AreNeverSame()
    {
        var snapshot = CreateSnapshot();
        var configuration = CreateConfiguration();

        Assert.False(HeadlessAnalysisKeyBuilder.IsSameBeatmapAndConfig(snapshot, configuration, null, null));
    }

    private static TosuBeatmapSnapshot CreateSnapshot(
        double rate = 1.0,
        string[]? mods = null,
        string? rawBeatmap = null)
    {
        var identity = new BeatmapIdentity("101", "hash-a", "7");
        var metadata = new TosuBeatmapMetadata
        {
            Artist = "Artist",
            Title = "Title",
            Version = "Version",
            Mapper = "Mapper"
        };
        return new TosuBeatmapSnapshot(
            identity,
            rawBeatmap ?? "osu file format v14\n[General]\nMode:3",
            metadata,
            rate,
            mods is null ? [] : mods.ToImmutableArray(),
            DateTimeOffset.UtcNow);
    }

    private static EffectiveAnalysisConfiguration CreateConfiguration(string algorithm = "Mixed")
    {
        return new EffectiveAnalysisConfiguration
        {
            DefaultAlgorithm = algorithm
        }.Normalize();
    }
}
