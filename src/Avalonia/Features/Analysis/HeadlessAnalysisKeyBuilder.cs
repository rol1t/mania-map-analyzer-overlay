using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Avalonia.Models;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

/// <summary>
/// Pure helper that builds the stable typed deduplication keys used by the
/// headless polling loop. Extracted so the key logic can be unit tested without
/// a running controller.
/// </summary>
public static class HeadlessAnalysisKeyBuilder
{
    public static HeadlessBeatmapKey BuildBeatmapKey(TosuBeatmapSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new HeadlessBeatmapKey(
            snapshot.Identity.Id,
            snapshot.Identity.StableKey,
            snapshot.Rate,
            CreateModsKey(snapshot.Mods),
            snapshot.RawBeatmap.Length);
    }

    public static HeadlessSceneKey BuildSceneKey(
        TosuBeatmapSnapshot snapshot,
        EffectiveAnalysisConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(configuration);
        EffectiveAnalysisConfiguration normalized = configuration.Normalize();

        return new HeadlessSceneKey(
            snapshot.Identity.StableKey,
            snapshot.Rate,
            CreateModsKey(snapshot.Mods),
            normalized.ConfigurationVersion,
            normalized.DefaultEngineId,
            normalized.DefaultAlgorithm,
            normalized.Widgets.Length,
            BuildConfigurationIdentity(normalized));
    }

    public static HeadlessAnalysisKey BuildAnalysisKey(
        TosuBeatmapSnapshot snapshot,
        EffectiveAnalysisConfiguration configuration)
    {
        return new HeadlessAnalysisKey(BuildBeatmapKey(snapshot), BuildSceneKey(snapshot, configuration));
    }

    /// <summary>
    /// Produces a canonical configuration identity for the Application
    /// runtime. This is an opaque causal token, not a display string; callers
    /// must pass it through unchanged and never infer behavior by parsing it.
    /// </summary>
    public static string BuildConfigurationIdentity(HeadlessAnalysisKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return key.SceneKey.ConfigurationIdentity;
    }

    public static string BuildConfigurationIdentity(EffectiveAnalysisConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EffectiveAnalysisConfiguration normalized = configuration.Normalize();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", normalized.SchemaVersion);
            writer.WriteString("configurationVersion", normalized.ConfigurationVersion);
            writer.WriteString("defaultEngineId", normalized.DefaultEngineId);
            writer.WriteString("defaultAlgorithm", normalized.DefaultAlgorithm);
            WriteOptions(writer, "defaultOptions", normalized.DefaultOptions);
            writer.WriteStartArray("widgets");
            foreach (EffectiveWidgetSpec widget in normalized.Widgets)
            {
                writer.WriteStartObject();
                writer.WriteString("widgetId", widget.WidgetId);
                writer.WriteStartArray("sources");
                foreach (EffectiveAnalysisSource source in widget.Sources)
                {
                    writer.WriteStartObject();
                    writer.WriteString("sourceId", source.SourceId);
                    writer.WriteString("engineId", source.EngineId);
                    writer.WriteString("requestedAlgorithm", source.RequestedAlgorithm);
                    writer.WriteString("configurationVersion", source.ConfigurationVersion);
                    WriteOptions(writer, "options", source.Options);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteStartArray("bindings");
                foreach (EffectiveWidgetBinding binding in widget.Bindings)
                {
                    writer.WriteStartObject();
                    writer.WriteString("targetMetricId", binding.TargetMetricId);
                    writer.WriteBoolean("allowsNull", binding.AllowsNull);
                    writer.WriteStartArray("candidates");
                    foreach (SourceMetricCandidate candidate in binding.Candidates)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("sourceId", candidate.SourceId);
                        writer.WriteString("metricId", candidate.MetricId);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return "sha256:" + Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    public static bool IsSameBeatmapAndConfig(
        TosuBeatmapSnapshot snapshot,
        EffectiveAnalysisConfiguration configuration,
        HeadlessAnalysisKey? lastAnalysisKey,
        HeadlessSceneKey? lastSceneKey)
    {
        var analysisKey = BuildAnalysisKey(snapshot, configuration);
        var sceneKey = analysisKey.SceneKey;

        return analysisKey.Equals(lastAnalysisKey) && sceneKey.Equals(lastSceneKey);
    }

    public static bool IsNewSceneGeneration(
        TosuBeatmapSnapshot snapshot,
        EffectiveAnalysisConfiguration configuration,
        HeadlessSceneKey? lastSceneKey)
    {
        var sceneKey = BuildSceneKey(snapshot, configuration);
        return !sceneKey.Equals(lastSceneKey);
    }

    /// <summary>
    /// Returns whether two analysis keys refer to the same verified beatmap
    /// file. Rate, modifiers and effective configuration are deliberately not
    /// part of this comparison: changing those values is an explicit user
    /// action and can be analysed immediately, while a carousel map change
    /// still needs the controller's transient-observation guard.
    /// </summary>
    public static bool IsSameBeatmapRevision(
        HeadlessAnalysisKey left,
        HeadlessAnalysisKey right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return string.Equals(
                left.BeatmapKey.StableKey,
                right.BeatmapKey.StableKey,
                StringComparison.OrdinalIgnoreCase)
            && left.BeatmapKey.RawBeatmapLength == right.BeatmapKey.RawBeatmapLength;
    }

    private static string CreateModsKey(System.Collections.Immutable.ImmutableArray<string> mods)
    {
        return string.Join(',', mods.OrderBy(static mod => mod, StringComparer.OrdinalIgnoreCase));
    }

    private static void WriteOptions(
        Utf8JsonWriter writer,
        string propertyName,
        ImmutableDictionary<string, JsonElement> options)
    {
        writer.WriteStartObject(propertyName);
        foreach (KeyValuePair<string, JsonElement> option in options.OrderBy(
                     static entry => entry.Key,
                     StringComparer.Ordinal))
        {
            writer.WritePropertyName(option.Key);
            WriteCanonicalJson(writer, option.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject().OrderBy(
                             static property => property.Name,
                             StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported JSON value kind '{value.ValueKind}' in effective analysis options.");
        }
    }
}
