using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public enum ReplayDiagnosticSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2
}

public sealed record ReplayDiagnostic
{
    public ReplayDiagnostic(
        ReplayDiagnosticSeverity severity,
        string code,
        string message,
        string? technicalDetails = null,
        ImmutableDictionary<string, string>? properties = null)
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
        Properties = (properties ?? ImmutableDictionary<string, string>.Empty)
            .ToImmutableDictionary(
                pair => pair.Key.Trim(),
                pair => pair.Value?.Trim() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }

    public ReplayDiagnosticSeverity Severity
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

    public static ReplayDiagnostic Error(string code, string message, Exception? exception = null)
    {
        return new ReplayDiagnostic(ReplayDiagnosticSeverity.Error, code, message, exception?.ToString());
    }
}
