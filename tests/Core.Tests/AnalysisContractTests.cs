using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Core.Tests;

public sealed class AnalysisContractTests
{
    [Fact]
    public void ConfigurationRoundTripPreservesTypedJsonValues()
    {
        AnalysisConfiguration configuration;
        using (var document = JsonDocument.Parse(
            """
            {
              "enabled": false,
              "threshold": 12.5,
              "pattern": {
                "clusters": [
                  { "name": "stream", "weights": [1, 2, 3] }
                ]
              }
            }
            """))
        {
            configuration = new AnalysisConfiguration(
                "Mixed",
                "config-1",
                document.RootElement
                    .EnumerateObject()
                    .Select(property => new KeyValuePair<string, JsonElement>(property.Name, property.Value)));
        }

        var serialized = JsonSerializer.Serialize(configuration);
        var restored = JsonSerializer.Deserialize<AnalysisConfiguration>(serialized);

        Assert.NotNull(restored);
        Assert.False(restored.Options["enabled"].GetBoolean());
        Assert.Equal(12.5, restored.Options["threshold"].GetDouble());
        var cluster = restored.Options["pattern"].GetProperty("clusters")[0];
        Assert.Equal("stream", cluster.GetProperty("name").GetString());
        Assert.Equal(3, cluster.GetProperty("weights").GetArrayLength());
    }

    [Fact]
    public void StructuredSemanticMetricRoundTripsWithoutLosingTypes()
    {
        var metric = SemanticMetric.FromValue(
            "pattern.clusters",
            new
            {
                enabled = false,
                confidence = 0.875,
                clusters = new[]
                {
                    new
                    {
                        name = "jumpstream",
                        values = new[] { 1.25, 2.5 }
                    }
                }
            });

        var serialized = JsonSerializer.Serialize(metric);
        var restored = JsonSerializer.Deserialize<SemanticMetric>(serialized);

        Assert.NotNull(restored);
        Assert.False(restored.Value.GetProperty("enabled").GetBoolean());
        Assert.Equal(0.875, restored.Value.GetProperty("confidence").GetDouble());
        var cluster = restored.Value.GetProperty("clusters")[0];
        Assert.Equal("jumpstream", cluster.GetProperty("name").GetString());
        Assert.Equal(2, cluster.GetProperty("values").GetArrayLength());
    }

    [Fact]
    public void CanonicalConfigurationIgnoresJsonObjectPropertyOrder()
    {
        var first = RequestWithStructuredOption("""{"enabled":false,"weights":[1,2]}""");
        var reordered = RequestWithStructuredOption("""{"weights":[1,2],"enabled":false}""");

        Assert.Equal(first.Key, reordered.Key);
    }

    [Fact]
    public void ConfigurationIdentityEscapesAlgorithmVersionAndOptionNames()
    {
        var algorithmWithNewline = RequestWithIdentityParts("Mixed\nconfig", "1", "enabled", true);
        var versionWithNewline = RequestWithIdentityParts("Mixed", "config\n1", "enabled", true);
        var optionNameWithDelimiter = RequestWithIdentityParts("Mixed", "config", "enabled=true\nother", false);
        var separateOptions = RequestWithIdentityParts(
            "Mixed",
            "config",
            "enabled",
            JsonSerializer.SerializeToElement("true\nother=false"));

        Assert.NotEqual(algorithmWithNewline.Key, versionWithNewline.Key);
        Assert.NotEqual(optionNameWithDelimiter.Key, separateOptions.Key);
    }

    private static AnalysisRequest RequestWithStructuredOption(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new AnalysisRequest(
            "engine",
            new BeatmapIdentity("map-a", "hash-map-a"),
            "osu file contents for map-a",
            "Mixed",
            "widget",
            options:
            [
                new KeyValuePair<string, JsonElement>("pattern", document.RootElement)
            ],
            configurationVersion: "config-1");
    }

    private static AnalysisRequest RequestWithIdentityParts<T>(
        string algorithm,
        string version,
        string optionName,
        T optionValue)
    {
        return new AnalysisRequest(
            "engine",
            new BeatmapIdentity("map-a", "hash-map-a"),
            "osu file contents for map-a",
            algorithm,
            "widget",
            options:
            [
                new KeyValuePair<string, JsonElement>(
                    optionName,
                    optionValue is JsonElement element
                        ? element
                        : JsonSerializer.SerializeToElement(optionValue))
            ],
            configurationVersion: version);
    }
}
