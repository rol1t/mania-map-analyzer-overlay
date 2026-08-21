using System.Security.Cryptography;
using System.Text;

namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Resolves a request to a registered engine. Selection is explicit in the
/// request so a preset can choose an engine and an algorithm independently.
/// </summary>
public sealed class AnalyzerExecutionPlanner
{
    private readonly IReadOnlyDictionary<string, IAnalyzerEngine> _engines;

    public AnalyzerExecutionPlanner(IEnumerable<IAnalyzerEngine> engines)
    {
        ArgumentNullException.ThrowIfNull(engines);

        _engines = engines.ToDictionary(
            engine => engine.Descriptor.Id,
            StringComparer.OrdinalIgnoreCase);

        if (_engines.Count == 0)
        {
            throw new ArgumentException("At least one analyzer engine is required.", nameof(engines));
        }
    }

    public IReadOnlyCollection<AnalyzerEngineDescriptor> AvailableEngines =>
        _engines.Values.Select(engine => engine.Descriptor).ToArray();

    public AnalyzerExecutionPlan CreatePlan(AnalysisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_engines.TryGetValue(request.EngineId, out var engine))
        {
            throw new KeyNotFoundException($"Analyzer engine '{request.EngineId}' is not registered.");
        }

        ValidateCapabilities(request, engine.Descriptor);
        var executionKey = AnalysisExecutionKey.Create(request, engine.Descriptor);
        return new AnalyzerExecutionPlan(request, engine, executionKey);
    }

    private static void ValidateCapabilities(
        AnalysisRequest request,
        AnalyzerEngineDescriptor descriptor)
    {
        var capabilities = descriptor.Capabilities;
        if (!capabilities.SupportedAlgorithms.IsEmpty
            && !capabilities.SupportedAlgorithms.Contains(
                request.RequestedAlgorithm,
                StringComparer.Ordinal))
        {
            throw new NotSupportedException(
                $"Analyzer engine '{descriptor.Id}' does not support the case-sensitive algorithm " +
                $"'{request.RequestedAlgorithm}'.");
        }

        if (!capabilities.SupportsRate && request.Rate != AnalysisRequest.DefaultRate)
        {
            throw new NotSupportedException(
                $"Analyzer engine '{descriptor.Id}' does not support rate changes, but rate " +
                $"'{AnalysisRequest.FormatRate(request.Rate)}' was requested.");
        }

        if (!capabilities.SupportsMods && !request.Mods.IsEmpty)
        {
            throw new NotSupportedException(
                $"Analyzer engine '{descriptor.Id}' does not support mods, but " +
                $"'{string.Join(", ", request.Mods)}' was requested.");
        }

        if (request.Options.ContainsKey(AnalysisRequest.ReservedSpeedRateOptionName))
        {
            throw new InvalidOperationException(
                $"Analyzer option '{AnalysisRequest.ReservedSpeedRateOptionName}' is reserved. " +
                $"Use {nameof(AnalysisRequest)}.{nameof(AnalysisRequest.Rate)} as the canonical speed input.");
        }
    }
}

public sealed record AnalyzerExecutionPlan(
    AnalysisRequest Request,
    IAnalyzerEngine Engine,
    AnalysisExecutionKey ExecutionKey);

/// <summary>
/// Version-aware identity for one engine execution. It combines the normalized
/// request with the installed engine, upstream analyzer, and configuration
/// contract versions.
/// </summary>
public readonly record struct AnalysisExecutionKey(string Value)
{
    public override string ToString() => Value;

    internal static AnalysisExecutionKey Create(
        AnalysisRequest request,
        AnalyzerEngineDescriptor descriptor)
    {
        var identity = string.Join(
            '\n',
            request.Key.Value,
            descriptor.Id.ToUpperInvariant(),
            descriptor.Version,
            descriptor.UpstreamVersion,
            request.Configuration.Version);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new AnalysisExecutionKey(Convert.ToHexString(bytes));
    }
}
