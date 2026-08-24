using System;

namespace ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;

/// <summary>
/// Indicates that tosu could not provide a consistent analyzer input.
/// Callers can surface the message while the original exception remains
/// available for diagnostics and logging.
/// </summary>
public sealed class TosuBeatmapSourceException : Exception
{
    public TosuBeatmapSourceException(string message)
        : base(message)
    {
    }

    public TosuBeatmapSourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TosuBeatmapSourceException(string message, string route, System.Net.HttpStatusCode statusCode)
        : base(message)
    {
        Route = route;
        StatusCode = statusCode;
    }

    /// <summary>Endpoint and HTTP status when this was raised by the adapter.</summary>
    public string?
        Route
    {
        get;
    }

    public System.Net.HttpStatusCode?
        StatusCode
    {
        get;
    }
}
