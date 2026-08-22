using System;
using System.Collections.Immutable;
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
        var snapshot = CreateSnapshot();
        var configuration = CreateConfiguration();

        var key1 = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(snapshot, configuration);
        var key2 = HeadlessAnalysisKeyBuilder.BuildAnalysisKey(snapshot, configuration);

        Assert.Equal(key1, key2);
        Assert.True(HeadlessAnalysisKeyBuilder.IsSameBeatmapAndConfig(snapshot, configuration, key1, key1.SceneKey));
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
