using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

public sealed partial class AnalyzerEngineScriptBridge
{
    private AnalysisResult MapResponse(JsonElement message, string type, AnalysisRequest request)
    {
        if (string.Equals(type, "analysis.cancelled", StringComparison.Ordinal))
        {
            var diagnostic = MapDiagnostics(message, "diagnostics").FirstOrDefault() ??
                new AnalysisDiagnostic(
                    AnalysisDiagnosticSeverity.Information,
                    "analysis.cancelled",
                    "The analyzer cancelled the request.");
            Report(diagnostic);
            return AnalysisResult.Cancelled(request, Descriptor, diagnostic);
        }

        if (string.Equals(type, "analysis.error", StringComparison.Ordinal))
        {
            var diagnostic = MapError(message) ??
                AnalysisDiagnostic.Error(
                    "analysis.failed",
                    $"Analyzer engine '{Descriptor.Id}' returned an analysis error.");
            Report(diagnostic);
            return AnalysisResult.Failure(request, Descriptor, diagnostic);
        }

        var diagnostics = MapDiagnostics(message, "diagnostics").ToList();
        var analysis = message.TryGetProperty("analysis", out var analysisElement) &&
                       analysisElement.ValueKind == JsonValueKind.Object
            ? analysisElement
            : default;
        if (analysis.ValueKind != JsonValueKind.Object)
        {
            var diagnostic = AnalysisDiagnostic.Error(
                "analysis.result_missing",
                $"Analyzer engine '{Descriptor.Id}' returned a result without analysis data.");
            Report(diagnostic);
            return AnalysisResult.Failure(request, Descriptor, diagnostic);
        }

        diagnostics = diagnostics.Concat(MapDiagnostics(analysis, "diagnostics"))
            .GroupBy(diagnostic => diagnostic.Code + "\n" + diagnostic.Message, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var requestedAlgorithm = GetString(analysis, "requestedAlgorithm") ?? request.RequestedAlgorithm;
        var actualAlgorithm = GetString(analysis, "actualAlgorithm") ?? GetString(message, "actualAlgorithm");
        var status = GetString(message, "status");
        var outcome = string.Equals(status, "partial", StringComparison.OrdinalIgnoreCase)
            ? AnalysisOutcome.Partial
            : AnalysisOutcome.Success;

        if (string.IsNullOrWhiteSpace(actualAlgorithm))
        {
            var diagnostic = AnalysisDiagnostic.Error(
                "analysis.actual_algorithm_missing",
                $"Analyzer engine '{Descriptor.Id}' returned a successful result without the actual algorithm.");
            Report(diagnostic);
            return AnalysisResult.Failure(request, Descriptor, diagnostic);
        }

        var metrics = MapMetrics(analysis, diagnostics);
        if (outcome == AnalysisOutcome.Success &&
            diagnostics.Any(diagnostic => diagnostic.Severity == AnalysisDiagnosticSeverity.Error))
        {
            outcome = AnalysisOutcome.Partial;
        }

        foreach (var diagnostic in diagnostics)
        {
            Report(diagnostic);
        }

        return new AnalysisResult(
            request.Key,
            Descriptor.Id,
            requestedAlgorithm,
            actualAlgorithm,
            metrics,
            diagnostics,
            outcome);
    }

    private IEnumerable<SemanticMetric> MapMetrics(
        JsonElement analysis,
        ICollection<AnalysisDiagnostic> diagnostics)
    {
        if (!analysis.TryGetProperty("metrics", out var metricsElement) ||
            metricsElement.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<SemanticMetric>();
        }

        var result = new List<SemanticMetric>();
        foreach (var property in metricsElement.EnumerateObject())
        {
            try
            {
                var metricElement = property.Value;
                var id = property.Name;
                var value = metricElement;
                var unit = string.Empty;
                var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (metricElement.ValueKind == JsonValueKind.Object)
                {
                    id = GetString(metricElement, "id") ?? property.Name;
                    if (metricElement.TryGetProperty("value", out var valueElement))
                    {
                        value = valueElement;
                    }

                    unit = GetString(metricElement, "unit") ?? string.Empty;
                    if (metricElement.TryGetProperty("metadata", out var metadataElement) &&
                        metadataElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var item in metadataElement.EnumerateObject())
                        {
                            metadata[item.Name] = item.Value.ToString();
                        }
                    }
                }

                if (value.ValueKind == JsonValueKind.Undefined)
                {
                    throw new InvalidDataException($"Metric '{id}' has no value.");
                }

                result.Add(new SemanticMetric(id, value, unit, metadata.ToImmutableDictionary()));
            }
            catch (Exception exception)
            {
                var diagnostic = AnalysisDiagnostic.Error(
                    "analysis.metric_invalid",
                    $"Analyzer engine '{Descriptor.Id}' returned an invalid metric '{property.Name}'.",
                    exception,
                    [new KeyValuePair<string, string>("metricId", property.Name)]);
                diagnostics.Add(diagnostic);
                Report(diagnostic, exception);
            }
        }

        return result;
    }

    private IReadOnlyList<AnalysisDiagnostic> MapDiagnostics(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var diagnosticsElement) ||
            diagnosticsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AnalysisDiagnostic>();
        }

        var diagnostics = new List<AnalysisDiagnostic>();
        foreach (var element in diagnosticsElement.EnumerateArray())
        {
            try
            {
                var code = GetString(element, "code") ?? "ANALYSIS_DIAGNOSTIC";
                var message = GetString(element, "message") ?? "Analyzer diagnostic.";
                var severity = ParseSeverity(GetString(element, "severity"));
                var technicalDetails = element.TryGetProperty("details", out var details)
                    ? details.ToString()
                    : null;
                diagnostics.Add(new AnalysisDiagnostic(severity, code, message, technicalDetails));
            }
            catch (Exception exception)
            {
                var diagnostic = AnalysisDiagnostic.Error(
                    "engine.diagnostic_invalid",
                    $"Analyzer engine '{Descriptor.Id}' returned an invalid diagnostic.",
                    exception);
                diagnostics.Add(diagnostic);
                Report(diagnostic, exception);
            }
        }

        return diagnostics;
    }

    private AnalysisDiagnostic? MapError(JsonElement message)
    {
        if (!message.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var code = GetString(error, "code") ?? "ANALYSIS_FAILED";
        var errorMessage = GetString(error, "message") ?? "Analyzer engine failed.";
        var stage = GetString(error, "stage");
        var details = error.TryGetProperty("details", out var detailsElement)
            ? detailsElement.ToString()
            : null;
        return new AnalysisDiagnostic(
            AnalysisDiagnosticSeverity.Error,
            code,
            errorMessage,
            string.IsNullOrWhiteSpace(stage) ? details : stage + ": " + details);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static AnalysisDiagnosticSeverity ParseSeverity(string? severity)
    {
        return severity?.ToLowerInvariant() switch
        {
            "error" => AnalysisDiagnosticSeverity.Error,
            "info" or "information" => AnalysisDiagnosticSeverity.Information,
            _ => AnalysisDiagnosticSeverity.Warning
        };
    }
}
