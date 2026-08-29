using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

/// <summary>
/// Small repository-level guardrails for the incremental architecture cutover.
/// These tests deliberately check only stable boundaries; they do not encode
/// a cosmetic folder layout or require the final migration to happen at once.
/// </summary>
public sealed class ArchitectureConventionTests
{
    [Fact]
    public void DomainAndApplicationProjectsKeepPlatformDependenciesPointingOutward()
    {
        string applicationProject = ReadRepositoryFile(
            Path.Combine("src", "Application", "ManiaMapAnalyzerOverlay.Application.csproj"));
        string realtimeProject = ReadRepositoryFile(
            Path.Combine("src", "RealtimeAnalysis", "ManiaMapAnalyzerOverlay.RealtimeAnalysis.csproj"));
        string replayProject = ReadRepositoryFile(
            Path.Combine("src", "ReplayAnalysis", "ManiaMapAnalyzerOverlay.ReplayAnalysis.csproj"));

        Assert.DoesNotContain("Avalonia", applicationProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Avalonia", realtimeProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Avalonia", replayProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReplayAnalysis", applicationProject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RealtimeAnalysis", applicationProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReplayAnalysis", realtimeProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RealtimeAnalysis", replayProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Application", replayProject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RawTosuJsonNormalizationStaysAtTheAvaloniaInfrastructureBoundary()
    {
        string collector = ReadRepositoryFile(
            Path.Combine("src", "Avalonia", "Infrastructure", "Tosu", "TosuRealtimeCollector.cs"));
        string realtimeContract = ReadRepositoryFile(
            Path.Combine("src", "RealtimeAnalysis", "RealtimeTelemetryUpdate.cs"));
        string realtimeAnalyzer = ReadRepositoryFile(
            Path.Combine("src", "RealtimeAnalysis", "RealtimePauseCoach.cs"));

        Assert.Contains("JsonElement", collector, StringComparison.Ordinal);
        Assert.Contains("TosuRealtimePayloadNormalizer", collector, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonElement", realtimeContract, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Text.Json", realtimeContract, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonElement", realtimeAnalyzer, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Text.Json", realtimeAnalyzer, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererDoesNotOpenRealtimeTransports()
    {
        string renderer = ReadRepositoryFile(Path.Combine("assets", "overlay", "runtime", "renderer.js"));

        Assert.DoesNotContain("fetch(", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("XMLHttpRequest", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("new WebSocket", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("__createRealtimePauseCoachRuntime", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void TosuTransportAndTemporaryBrowserRuntimeStayAtAdapterBoundary()
    {
        string adapter = ReadRepositoryFile(
            Path.Combine("assets", "analyzers", "mania-map-analyser", "adapter.js"));
        string pauseCoachRuntime = ReadRepositoryFile(
            Path.Combine("assets", "overlay", "runtime", "pause-coach.js"));

        Assert.Contains("fetch(", adapter, StringComparison.Ordinal);
        Assert.Contains("new WebSocket", adapter, StringComparison.Ordinal);
        Assert.Contains("__createRealtimePauseCoachRuntime", adapter, StringComparison.Ordinal);
        Assert.Contains("function create(options)", pauseCoachRuntime, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(relativePath).ToArray());
            if (File.Exists(Path.Combine(directory.FullName, "ManiaMapAnalyzerOverlay.sln")) && File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the repository root from '{AppContext.BaseDirectory}'.");
    }
}
