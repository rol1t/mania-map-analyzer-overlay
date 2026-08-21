using System;
using System.Collections.Generic;
using System.Linq;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

public enum AnalyzerEngineDiagnosticSeverity
{
    Error,
    Warning,
    Information
}

/// <summary>
/// A machine-readable package discovery problem. Diagnostics are retained by
/// the catalog so a host can explain why an engine is unavailable without
/// attempting to execute it.
/// </summary>
public sealed class AnalyzerEngineDiagnostic
{
    public AnalyzerEngineDiagnostic(
        string code,
        string message,
        string? path = null,
        AnalyzerEngineDiagnosticSeverity severity = AnalyzerEngineDiagnosticSeverity.Error,
        Exception? exception = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("A diagnostic code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A diagnostic message is required.", nameof(message));
        }

        Code = code.Trim();
        Message = message.Trim();
        Path = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        Severity = severity;
        Exception = exception;
        ExceptionType = exception?.GetType().FullName;
    }

    public string Code
    {
        get;
    }

    public string Message
    {
        get;
    }

    public string? Path
    {
        get;
    }

    public AnalyzerEngineDiagnosticSeverity Severity
    {
        get;
    }

    public string? ExceptionType
    {
        get;
    }

    internal Exception? Exception
    {
        get;
    }

    public override string ToString() => Code + ": " + Message;
}

/// <summary>
/// Receives package-discovery diagnostics. Production uses the application
/// logger; hosts and tests can inject a reporter to collect or display the
/// structured entries without changing catalog behavior.
/// </summary>
public interface IAnalyzerEngineDiagnosticSink
{
    void Report(string operation, AnalyzerEngineDiagnostic diagnostic, Exception? exception = null);
}

public enum AnalyzerEngineAvailabilityStatus
{
    Available,
    Missing,
    Invalid,
    Unavailable
}

/// <summary>Availability and diagnostics for one discovered engine package.</summary>
public sealed class AnalyzerEngineAvailability
{
    public AnalyzerEngineAvailability(
        AnalyzerEngineAvailabilityStatus status,
        IEnumerable<AnalyzerEngineDiagnostic>? diagnostics = null)
    {
        Status = status;
        Diagnostics = (diagnostics ?? Array.Empty<AnalyzerEngineDiagnostic>()).ToArray();
    }

    public AnalyzerEngineAvailabilityStatus Status
    {
        get;
    }

    public bool IsAvailable => Status == AnalyzerEngineAvailabilityStatus.Available;

    public IReadOnlyList<AnalyzerEngineDiagnostic> Diagnostics
    {
        get;
    }
}
