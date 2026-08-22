using System;
using System.Collections.Immutable;
using ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class HeadlessBeatmapModeTests
{
    [Fact]
    public void RawMode3_IsNotExplicitlyNonMania()
    {
        var snapshot = CreateSnapshot(rawBeatmap: "osu file format v14\n[General]\nMode:3", metadataMode: "osu");
        Assert.False(HeadlessBeatmapMode.IsExplicitlyNonMania(snapshot));
    }

    [Fact]
    public void RawMode0_IsExplicitlyNonMania()
    {
        var snapshot = CreateSnapshot(rawBeatmap: "osu file format v14\n[General]\nMode:0", metadataMode: "mania");
        Assert.True(HeadlessBeatmapMode.IsExplicitlyNonMania(snapshot));
    }

    [Fact]
    public void MetadataOsu_WhenRawMissing_IsExplicitlyNonMania()
    {
        var snapshot = CreateSnapshot(rawBeatmap: "osu file format v14\n[General]\nAudioFilename: audio.mp3", metadataMode: "osu");
        Assert.True(HeadlessBeatmapMode.IsExplicitlyNonMania(snapshot));
    }

    [Fact]
    public void UnknownOrMissing_IsNotExplicitlyNonMania()
    {
        var missingBoth = CreateSnapshot(rawBeatmap: "osu file format v14\n[General]\nAudioFilename: audio.mp3", metadataMode: "");
        Assert.False(HeadlessBeatmapMode.IsExplicitlyNonMania(missingBoth));

        var missingRawNoMeta = CreateSnapshot(rawBeatmap: "osu file format v14\n[General]\nAudioFilename: audio.mp3", metadataMode: null);
        Assert.False(HeadlessBeatmapMode.IsExplicitlyNonMania(missingRawNoMeta));

        var unknownRawUnknownMeta = CreateSnapshot(rawBeatmap: "osu file format v14\n[General]\nMode:", metadataMode: "");
        Assert.False(HeadlessBeatmapMode.IsExplicitlyNonMania(unknownRawUnknownMeta));
    }

    [Fact]
    public void RawManiaText_IsNotExplicitlyNonMania()
    {
        var snapshot = CreateSnapshot(rawBeatmap: "osu file format v14\n[General]\nMode:mania", metadataMode: "osu");
        Assert.False(HeadlessBeatmapMode.IsExplicitlyNonMania(snapshot));
    }

    [Fact]
    public void RawModeTakesPrecedenceOverMetadata()
    {
        var rawManiaMetaOsu = CreateSnapshot(rawBeatmap: "osu file format v14\n[General]\nMode:3", metadataMode: "osu");
        Assert.False(HeadlessBeatmapMode.IsExplicitlyNonMania(rawManiaMetaOsu));

        var rawOsuMetaMania = CreateSnapshot(rawBeatmap: "osu file format v14\n[General]\nMode:0", metadataMode: "mania");
        Assert.True(HeadlessBeatmapMode.IsExplicitlyNonMania(rawOsuMetaMania));
    }

    private static TosuBeatmapSnapshot CreateSnapshot(string rawBeatmap, string? metadataMode)
    {
        var identity = new BeatmapIdentity("101", "hash-a", "7");
        var metadata = new TosuBeatmapMetadata
        {
            Title = "Title",
            Version = "Version",
            Mode = metadataMode ?? string.Empty
        };

        return new TosuBeatmapSnapshot(
            identity,
            rawBeatmap,
            metadata,
            1.0,
            ImmutableArray<string>.Empty,
            DateTimeOffset.UtcNow);
    }
}
