using System.Net;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class TosuRealtimePayloadResultTests
{
    [Fact]
    public void SuccessCarriesAnObjectPayload()
    {
        using JsonDocument document = JsonDocument.Parse("{\"state\":{\"name\":\"Play\"}}");

        TosuRealtimePayloadResult result = TosuRealtimePayloadResult.Success(document.RootElement.Clone());

        Assert.True(result.IsSuccess);
        Assert.Equal(TosuRealtimePayloadFailureKind.None, result.FailureKind);
        Assert.Equal(JsonValueKind.Object, result.Payload!.Value.ValueKind);
    }

    [Fact]
    public void HttpFailurePreservesTypedStatusAndDetail()
    {
        TosuRealtimePayloadResult result = TosuRealtimePayloadResult.Failure(
            TosuRealtimePayloadFailureKind.HttpFailure,
            HttpStatusCode.NotFound,
            "json/v2 is unavailable");

        Assert.False(result.IsSuccess);
        Assert.Equal(TosuRealtimePayloadFailureKind.HttpFailure, result.FailureKind);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal("json/v2 is unavailable", result.Detail);
        Assert.Null(result.Payload);
    }

    [Fact]
    public void EmptyResponseIsDistinctFromMalformedJson()
    {
        TosuRealtimePayloadResult result = TosuRealtimePayloadResult.Failure(
            TosuRealtimePayloadFailureKind.EmptyResponse,
            detail: "json/v2 returned an empty response.");

        Assert.False(result.IsSuccess);
        Assert.Equal(TosuRealtimePayloadFailureKind.EmptyResponse, result.FailureKind);
        Assert.Null(result.StatusCode);
    }
}
