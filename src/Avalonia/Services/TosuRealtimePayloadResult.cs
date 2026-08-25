using System;
using System.Net;
using System.Text.Json;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

public enum TosuRealtimePayloadFailureKind
{
    None,
    TransportUnavailable,
    HttpFailure,
    MalformedPayload,
    EmptyResponse
}

/// <summary>
/// Typed outcome of one native Tosu realtime transport read. A missing payload
/// is expected during short process/startup transitions, but callers still get
/// a reason and optional HTTP status instead of inferring it from a null value
/// or a localized exception message.
/// </summary>
public sealed record TosuRealtimePayloadResult(
    JsonElement? Payload,
    TosuRealtimePayloadFailureKind FailureKind = TosuRealtimePayloadFailureKind.None,
    HttpStatusCode? StatusCode = null,
    string? Detail = null)
{
    public bool IsSuccess => FailureKind == TosuRealtimePayloadFailureKind.None && Payload is not null;

    public static TosuRealtimePayloadResult Success(JsonElement payload) =>
        new(payload);

    public static TosuRealtimePayloadResult Failure(
        TosuRealtimePayloadFailureKind failureKind,
        HttpStatusCode? statusCode = null,
        string? detail = null) =>
        new(null, failureKind, statusCode, detail);
}
