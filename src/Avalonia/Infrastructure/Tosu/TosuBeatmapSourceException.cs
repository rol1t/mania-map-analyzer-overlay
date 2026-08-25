using System;

namespace ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;

public enum TosuBeatmapSourceFailureKind
{
    Unknown,
    TransportUnavailable,
    HttpFailure,
    OsuNotRunning,
    NoBeatmap,
    BeatmapChanged,
    MalformedPayload,
    EmptyResponse
}

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
        FailureKind = TosuBeatmapSourceFailureKind.Unknown;
    }

    public TosuBeatmapSourceException(
        string message,
        Exception innerException,
        TosuBeatmapSourceFailureKind failureKind = TosuBeatmapSourceFailureKind.Unknown)
        : base(message, innerException)
    {
        if (failureKind != TosuBeatmapSourceFailureKind.Unknown)
        {
            FailureKind = failureKind;
            return;
        }

        // Preserve structured transport/source context when an adapter layer
        // adds a human-readable wrapper. Runtime policy must not need to
        // rediscover the category from localized exception text.
        if (innerException is TosuBeatmapSourceException nested)
        {
            FailureKind = nested.FailureKind;
            Route = nested.Route;
            StatusCode = nested.StatusCode;
            return;
        }

        FailureKind = TosuBeatmapSourceFailureKind.Unknown;
    }

    public TosuBeatmapSourceException(
        string message,
        string route,
        System.Net.HttpStatusCode statusCode,
        TosuBeatmapSourceFailureKind failureKind = TosuBeatmapSourceFailureKind.Unknown)
        : base(message)
    {
        Route = route;
        StatusCode = statusCode;
        FailureKind = failureKind == TosuBeatmapSourceFailureKind.Unknown
            ? InferFailureKind(route, statusCode)
            : failureKind;
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

    public TosuBeatmapSourceFailureKind FailureKind
    {
        get;
    }

    private static TosuBeatmapSourceFailureKind InferFailureKind(
        string route,
        System.Net.HttpStatusCode statusCode)
    {
        if (statusCode == System.Net.HttpStatusCode.InternalServerError &&
            string.Equals(route, "json/v2", StringComparison.OrdinalIgnoreCase))
        {
            return TosuBeatmapSourceFailureKind.OsuNotRunning;
        }

        if (statusCode == System.Net.HttpStatusCode.NotFound &&
            string.Equals(route, "files/beatmap/file", StringComparison.OrdinalIgnoreCase))
        {
            return TosuBeatmapSourceFailureKind.NoBeatmap;
        }

        return TosuBeatmapSourceFailureKind.HttpFailure;
    }
}
