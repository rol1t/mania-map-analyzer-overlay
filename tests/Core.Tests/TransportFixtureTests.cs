using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Core.Tests;

public sealed class TransportFixtureTests
{
    [Fact]
    public void RoundTripsScalarMetricValues()
    {
        var metrics = new[]
        {
            SemanticMetric.FromValue("difficulty.star", 5.17, "SR"),
            SemanticMetric.FromValue("feature.string", "Reform 4 mid/high"),
            SemanticMetric.FromValue("feature.bool", true),
            SemanticMetric.FromValue<string?>("feature.null", null),
        };

        foreach (var metric in metrics)
        {
            var json = JsonSerializer.Serialize(metric.Value);
            var parsed = JsonDocument.Parse(json).RootElement;
            var restored = new SemanticMetric(metric.Id, parsed, metric.Unit, metric.Metadata);
            Assert.Equal(metric.Id, restored.Id);
            Assert.Equal(metric.Value.GetRawText(), restored.Value.GetRawText());
        }
    }

    [Fact]
    public void RoundTripsStructuredMetricValue()
    {
        var payload = new
        {
            star = 5.17,
            lnPercent = 51.4,
            keys = 4,
            label = "Reform 4 mid/high"
        };
        var metric = SemanticMetric.FromValue("difficulty.structured", payload, "object");
        var json = JsonSerializer.Serialize(metric.Value);
        var parsed = JsonDocument.Parse(json).RootElement;
        Assert.Equal(JsonValueKind.Object, parsed.ValueKind);
        Assert.Equal(5.17, parsed.GetProperty("star").GetDouble());
        Assert.Equal("Reform 4 mid/high", parsed.GetProperty("label").GetString());
        var restored = new SemanticMetric(metric.Id, parsed, metric.Unit);
        Assert.Equal(metric.Value.GetRawText(), restored.Value.GetRawText());
    }

    [Fact]
    public void RoundTripsArrayMetricValue()
    {
        var values = new[] { 1.2, 3.4, 5.6 };
        var metric = SemanticMetric.FromValue("pattern.clusters", values);
        var json = JsonSerializer.Serialize(metric.Value);
        var parsed = JsonDocument.Parse(json).RootElement;
        Assert.Equal(JsonValueKind.Array, parsed.ValueKind);
        Assert.Equal(3, parsed.GetArrayLength());
        var restored = new SemanticMetric(metric.Id, parsed);
        Assert.Equal(metric.Value.GetRawText(), restored.Value.GetRawText());
    }

    [Fact]
    public void RoundTripsSeriesMetricValue()
    {
        var series = new[]
        {
            new { time = 1000, value = 4.2, label = "stream" },
            new { time = 2000, value = 5.1, label = "jumpstream" }
        };
        var metric = SemanticMetric.FromValue("chart.series", series);
        var json = JsonSerializer.Serialize(metric.Value);
        var parsed = JsonDocument.Parse(json).RootElement;
        Assert.Equal(JsonValueKind.Array, parsed.ValueKind);
        Assert.Equal(2, parsed.GetArrayLength());
        Assert.Equal("stream", parsed[0].GetProperty("label").GetString());
        var restored = new SemanticMetric(metric.Id, parsed, "series");
        Assert.Equal(metric.Value.GetRawText(), restored.Value.GetRawText());
    }

    [Fact]
    public void RoundTripsAnalysisResultWithMixedMetricKinds()
    {
        var request = new AnalysisRequest(
            "test-engine",
            new BeatmapIdentity("123", "abc"),
            "osu file",
            "Mixed",
            "test-profile");
        var metrics = new[]
        {
            SemanticMetric.FromValue("difficulty.star", 6.2, "SR"),
            SemanticMetric.FromValue("pattern.clusters", new[] { new { id = "a", value = 1 }, new { id = "b", value = 2 } }),
            SemanticMetric.FromValue("skills.stream", new { value = 19, normalized = 85 })
        };
        var result = new AnalysisResult(
            request.Key,
            "test-engine",
            "Mixed",
            "Roxy",
            metrics,
            [new AnalysisDiagnostic(AnalysisDiagnosticSeverity.Information, "test.info", "ok")],
            AnalysisOutcome.Success);

        var serialized = JsonSerializer.Serialize(new
        {
            result.RequestKey.Value,
            result.EngineId,
            result.RequestedAlgorithm,
            result.ActualAlgorithm,
            metrics = result.Metrics.ToDictionary(entry => entry.Key, entry => entry.Value.Value.GetRawText()),
            diagnostics = result.Diagnostics.Select(diagnostic => diagnostic.Code).ToArray()
        });
        var doc = JsonDocument.Parse(serialized);
        Assert.Equal("test-engine", doc.RootElement.GetProperty("EngineId").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("metrics").EnumerateObject().Count());
    }

    [Fact]
    public void PreservesMetricMetadataThroughClone()
    {
        var metric = SemanticMetric.FromValue(
            "difficulty.star",
            4.5,
            "SR",
            [new KeyValuePair<string, string>("source", "headless"), new KeyValuePair<string, string>("engineVersion", "1.0")]);
        var cloned = new SemanticMetric(metric.Id, metric.Value.Clone(), metric.Unit, metric.Metadata);
        Assert.Equal("headless", cloned.Metadata["source"]);
        Assert.Equal("1.0", cloned.Metadata["engineVersion"]);
    }
}
