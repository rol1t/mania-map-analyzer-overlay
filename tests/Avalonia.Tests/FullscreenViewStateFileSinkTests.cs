using System;
using System.IO;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class FullscreenViewStateFileSinkTests
{
    [Fact]
    public void ReplacesTheDocumentAtomicallyAndKeepsOnlyTheNewestVersion()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var sink = new FullscreenViewStateFileSink(directory);
            sink.Write(new OverlayViewState { Version = 1, BeatmapId = "674175" });
            sink.Write(new OverlayViewState { Version = 2, BeatmapId = "674175" });

            using var document = JsonDocument.Parse(File.ReadAllText(sink.ViewStatePath));
            Assert.Equal(2, document.RootElement.GetProperty("version").GetInt64());
            Assert.Equal("674175", document.RootElement.GetProperty("beatmapId").GetString());
            Assert.False(File.Exists(sink.ViewStatePath + ".tmp"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void ClearRemovesPublishedAndInterruptedTemporaryDocuments()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var sink = new FullscreenViewStateFileSink(directory);
            sink.Write(new OverlayViewState { Version = 3 });
            File.WriteAllText(sink.ViewStatePath + ".tmp", "partial");

            sink.Clear();

            Assert.False(File.Exists(sink.ViewStatePath));
            Assert.False(File.Exists(sink.ViewStatePath + ".tmp"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mania-map-analyzer-overlay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
