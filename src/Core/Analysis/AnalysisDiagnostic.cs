using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.Core.Analysis;

public enum AnalysisDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

/// <summary>
/// Structured diagnostic that can be logged by a platform host and shown by a
/// UI without requiring the Core project to depend on that host.
/// </summary>
public sealed record AnalysisDiagnostic
{
    public AnalysisDiagnostic(
        AnalysisDiagnosticSeverity severity,
        string code,
        string message,
        string? technicalDetails = null,
        IEnumerable<KeyValuePair<string, string>>? properties = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("A diagnostic code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A diagnostic message is required.", nameof(message));
        }

        Severity = severity;
        Code = code.Trim();
        Message = message.Trim();
        TechnicalDetails = technicalDetails?.Trim() ?? string.Empty;
        Properties = (properties ?? Array.Empty<KeyValuePair<string, string>>())
            .ToImmutableDictionary(
                property => property.Key.Trim(),
                property => property.Value?.Trim() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }

    public AnalysisDiagnosticSeverity Severity
    {
        get;
    }

    public string Code
    {
        get;
    }

    public string Message
    {
        get;
    }

    public string TechnicalDetails
    {
        get;
    }

    public ImmutableDictionary<string, string> Properties
    {
        get;
    }

    public static AnalysisDiagnostic Error(
        string code,
        string message,
        Exception? exception = null,
        IEnumerable<KeyValuePair<string, string>>? properties = null)
    {
        return new AnalysisDiagnostic(
            AnalysisDiagnosticSeverity.Error,
            code,
            message,
            exception?.ToString(),
            properties);
    }
}

/// <summary>
/// Core-facing sink for diagnostics. A platform host can bridge this to its
/// logger and user-facing error surface without introducing a UI dependency.
/// </summary>
public interface IAnalysisDiagnostics
{
    void Report(AnalysisDiagnostic diagnostic);
}

public sealed class NullAnalysisDiagnostics : IAnalysisDiagnostics
{
    public static NullAnalysisDiagnostics Instance { get; } = new();

    private NullAnalysisDiagnostics()
    {
    }

    public void Report(AnalysisDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
    }
}
