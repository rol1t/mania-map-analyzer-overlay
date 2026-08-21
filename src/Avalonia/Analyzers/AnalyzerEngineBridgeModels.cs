using System;
using System.Collections.Generic;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

/// <summary>
/// Describes the runtime bootstrap result returned by an analyzer script host.
/// </summary>
public sealed class AnalyzerEngineBridgeReady
{
    internal AnalyzerEngineBridgeReady(
        bool isPartial,
        IReadOnlyList<AnalysisDiagnostic> diagnostics)
    {
        IsPartial = isPartial;
        Diagnostics = diagnostics;
    }

    public bool IsPartial
    {
        get;
    }

    public IReadOnlyList<AnalysisDiagnostic> Diagnostics
    {
        get;
    }
}

/// <summary>
/// Identifies an analyzer bridge lifecycle or transport failure.
/// </summary>
public sealed class AnalyzerEngineBridgeException : Exception
{
    public AnalyzerEngineBridgeException(
        string message,
        Exception? innerException,
        AnalysisDiagnostic diagnostic)
        : base(message, innerException)
    {
        Diagnostic = diagnostic;
    }

    public AnalysisDiagnostic Diagnostic
    {
        get;
    }
}
