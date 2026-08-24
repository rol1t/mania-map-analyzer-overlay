using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class TosuBeatmapSourceTests
{
    [Fact]
    public async Task ReturnsStableRawBeatmapAndMetadata()
    {
        var handler = new RecordingHandler(
            JsonResponse(CreatePayload("101", "hash-a", "7", "play")),
            TextResponse("osu file content"),
            JsonResponse(CreatePayload("101", "hash-a", "7", "play")));
        var diagnostics = new RecordingDiagnostics();
        using var client = new HttpClient(handler);
        var source = new TosuBeatmapSource(client, new Uri("http://localhost:24050"), diagnostics);

        var snapshot = await source.GetCurrentAsync();

        Assert.Equal("101|HASH-A|7", snapshot.Identity.StableKey);
        Assert.Equal("osu file content", snapshot.RawBeatmap);
        Assert.Equal("Artist", snapshot.Metadata.Artist);
        Assert.Equal("Title", snapshot.Metadata.Title);
        Assert.Equal("Mapper", snapshot.Metadata.Mapper);
        Assert.Equal(174, snapshot.Metadata.Bpm);
        Assert.Equal(1.25, snapshot.Rate);
        Assert.Equal(["HD", "NC"], snapshot.Mods.ToArray());
        Assert.Empty(diagnostics.Entries);
        Assert.Equal(
            ["/json/v2", "/files/beatmap/file", "/json/v2"],
            handler.RequestedPaths);
    }

    [Fact]
    public async Task UsesCurrentMenuModsWhenPlayContainsStaleMods()
    {
        var payload = CreatePayload("101", "hash-a", "7", "play");
        var stateAndMenu = "\"state\": { \"name\": \"edit\" }, " +
                           "\"menu\": { \"mods\": { \"array\": [{ \"acronym\": \"DT\" }] } }, ";
        payload = payload.Insert(payload.IndexOf("\"play\"", StringComparison.Ordinal), stateAndMenu);
        var handler = new RecordingHandler(
            JsonResponse(payload),
            TextResponse("osu file content"),
            JsonResponse(payload));
        var diagnostics = new RecordingDiagnostics();
        using var client = new HttpClient(handler);
        var source = new TosuBeatmapSource(client, new Uri("http://localhost:24050"), diagnostics);

        var snapshot = await source.GetCurrentAsync();

        Assert.Equal(["DT"], snapshot.Mods.ToArray());
        Assert.Equal(1.5, snapshot.Rate);
    }

    [Fact]
    public async Task UsesResultsModsInsteadOfAnEmptyStalePlayObject()
    {
        const string payload = """
        {
          "state": { "name": "Results" },
          "beatmap": {
            "id": 101, "md5": "hash-a", "set": 7,
            "artist": "Artist", "title": "Title", "version": "Hyper", "creator": "Mapper",
            "bpm": 174, "overall_difficulty": 8.5, "circle_size": 4,
            "approach_rate": 9, "hp_drain": 7, "mode": "mania"
          },
          "play": { "mods": { "array": [] } },
          "resultsScreen": { "mods": { "array": [{ "acronym": "NC" }] } }
        }
        """;
        var handler = new RecordingHandler(
            JsonResponse(payload),
            TextResponse("osu file content"),
            JsonResponse(payload));
        using var client = new HttpClient(handler);
        var source = new TosuBeatmapSource(client, new Uri("http://localhost:24050"));

        var snapshot = await source.GetCurrentAsync();

        Assert.Equal(["NC"], snapshot.Mods.ToArray());
        Assert.Equal(1.5, snapshot.Rate);
    }

    [Fact]
    public async Task ReadsManiaKeyCountFromCurrentTosuV2StatsShape()
    {
        var payload = CreatePayload("101", "hash-a", "7", "menu")
            .Replace(
                "\"circle_size\": 4,",
                "\"stats\": { \"cs\": { \"original\": 4, \"converted\": 7 } },",
                StringComparison.Ordinal);
        var handler = new RecordingHandler(
            JsonResponse(payload),
            TextResponse("osu file content"),
            JsonResponse(payload));
        using var client = new HttpClient(handler);
        var source = new TosuBeatmapSource(client, new Uri("http://localhost:24050"));

        var snapshot = await source.GetCurrentAsync();

        Assert.Equal(7, snapshot.Metadata.CircleSize);
    }

    [Fact]
    public async Task IgnoresZeroConvertedKeyCountAndUsesOriginalCircleSize()
    {
        var payload = CreatePayload("101", "hash-a", "7", "menu")
            .Replace(
                "\"circle_size\": 4,",
                "\"stats\": { \"cs\": { \"original\": 4, \"converted\": 0 } },",
                StringComparison.Ordinal);
        var handler = new RecordingHandler(
            JsonResponse(payload),
            TextResponse("osu file content"),
            JsonResponse(payload));
        using var client = new HttpClient(handler);
        var source = new TosuBeatmapSource(client, new Uri("http://localhost:24050"));

        var snapshot = await source.GetCurrentAsync();

        Assert.Equal(4, snapshot.Metadata.CircleSize);
    }

    [Fact]
    public async Task RetriesWhenMapChangesDuringRawFileFetch()
    {
        var handler = new RecordingHandler(
            JsonResponse(CreatePayload("101", "hash-a", "7", "menu")),
            TextResponse("old map"),
            JsonResponse(CreatePayload("202", "hash-b", "8", "menu")),
            JsonResponse(CreatePayload("202", "hash-b", "8", "menu")),
            TextResponse("new map"),
            JsonResponse(CreatePayload("202", "hash-b", "8", "menu")));
        var diagnostics = new RecordingDiagnostics();
        using var client = new HttpClient(handler);
        var source = new TosuBeatmapSource(client, new Uri("http://localhost:24050"), diagnostics);

        var snapshot = await source.GetCurrentAsync();

        Assert.Equal("202|HASH-B|8", snapshot.Identity.StableKey);
        Assert.Equal("new map", snapshot.RawBeatmap);
        var warning = Assert.Single(diagnostics.Entries, entry => entry.Code == "tosu.beatmap_changed_during_fetch");
        Assert.Equal(AnalysisDiagnosticSeverity.Warning, warning.Severity);
    }

    [Fact]
    public async Task RejectsWhenMapNeverStabilizes()
    {
        var handler = new RecordingHandler(
            JsonResponse(CreatePayload("101", "hash-a", "7", "menu")),
            TextResponse("old map"),
            JsonResponse(CreatePayload("202", "hash-b", "8", "menu")));
        var diagnostics = new RecordingDiagnostics();
        using var client = new HttpClient(handler);
        var source = new TosuBeatmapSource(client, new Uri("http://localhost:24050"), diagnostics, 1);

        var exception = await Assert.ThrowsAsync<TosuBeatmapSourceException>(() => source.GetCurrentAsync());

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(diagnostics.Entries, entry => entry.Code == "tosu.beatmap_source_failed");
    }

    [Fact]
    public async Task ReportsMalformedJsonAndSurfacesFailure()
    {
        var handler = new RecordingHandler(JsonResponse("{malformed"));
        var diagnostics = new RecordingDiagnostics();
        using var client = new HttpClient(handler);
        var source = new TosuBeatmapSource(client, new Uri("http://localhost:24050"), diagnostics);

        var exception = await Assert.ThrowsAsync<TosuBeatmapSourceException>(() => source.GetCurrentAsync());

        Assert.Contains("malformed JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(diagnostics.Entries, entry => entry.Code == "tosu.beatmap_source_failed");
    }

    [Fact]
    public async Task ReportsNonSuccessResponseAndSurfacesFailure()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var diagnostics = new RecordingDiagnostics();
        using var client = new HttpClient(handler);
        var source = new TosuBeatmapSource(client, new Uri("http://localhost:24050"), diagnostics);

        var exception = await Assert.ThrowsAsync<TosuBeatmapSourceException>(() => source.GetCurrentAsync());

        Assert.Contains("HTTP 503", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(diagnostics.Entries, entry => entry.Code == "tosu.beatmap_source_failed");
    }

    [Fact]
    public async Task FallsBackToBeatmapPathWhenCurrentShortcutReturnsNotFound()
    {
        const string beatmapPath = @"3\38\Example.osu";
        var handler = new RecordingHandler(
            JsonResponse(CreatePayload("101", "hash-a", "7", "play", beatmapPath)),
            new HttpResponseMessage(HttpStatusCode.NotFound),
            TextResponse("fallback osu file"),
            JsonResponse(CreatePayload("101", "hash-a", "7", "play", beatmapPath)));
        var diagnostics = new RecordingDiagnostics();
        using var client = new HttpClient(handler);
        var source = new TosuBeatmapSource(client, new Uri("http://localhost:24050"), diagnostics);

        var snapshot = await source.GetCurrentAsync();

        Assert.Equal("fallback osu file", snapshot.RawBeatmap);
        Assert.Equal(
            ["/json/v2", "/files/beatmap/file", "/files/beatmap/3/38/Example.osu", "/json/v2"],
            handler.RequestedPaths);
        Assert.Contains(diagnostics.Entries, entry => entry.Code == "tosu.beatmap_file_fallback");
    }

    [Fact]
    public async Task TreatsMissingCurrentFileAsNoBeatmapInsteadOfSourceFailure()
    {
        var handler = new RecordingHandler(
            JsonResponse(CreatePayload("101", "hash-a", "7", "play")),
            new HttpResponseMessage(HttpStatusCode.NotFound));
        var diagnostics = new RecordingDiagnostics();
        using var client = new HttpClient(handler);
        var source = new TosuBeatmapSource(client, new Uri("http://localhost:24050"), diagnostics);

        var exception = await Assert.ThrowsAsync<TosuBeatmapSourceException>(() => source.GetCurrentAsync());

        Assert.Contains("HTTP 404", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(diagnostics.Entries, entry => entry.Code == "tosu.no_beatmap");
        Assert.DoesNotContain(diagnostics.Entries, entry => entry.Code == "tosu.beatmap_source_failed");
    }

    [Fact]
    public async Task PropagatesCallerCancellationWithoutReportingAsFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new BlockingHandler(cancellation.Token);
        var diagnostics = new RecordingDiagnostics();
        using var client = new HttpClient(handler);
        var source = new TosuBeatmapSource(client, new Uri("http://localhost:24050"), diagnostics);
        var task = source.GetCurrentAsync(cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.DoesNotContain(diagnostics.Entries, entry => entry.Code == "tosu.beatmap_source_failed");
    }

    private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage TextResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "text/plain")
    };

    private static string CreatePayload(
        string id,
        string hash,
        string setId,
        string section,
        string? beatmapPath = null)
    {
        var map = $$"""
            {
              "id": {{id}},
              "md5": {{System.Text.Json.JsonSerializer.Serialize(hash)}},
              "set": {{setId}},
              "artist": "Artist",
              "title": "Title",
              "version": "Hyper",
              "creator": "Mapper",
              "bpm": 174,
              "overall_difficulty": 8.5,
              "circle_size": 4,
              "approach_rate": 9,
              "hp_drain": 7,
              "mode": "mania"
            }
            """;
        var mods = section == "play"
            ? "\"mods\":{\"array\":[{\"acronym\":\"NC\"},{\"acronym\":\"HD\",\"settings\":{\"speed_change\":1.25}}]}"
            : "\"mods\":{\"array\":[]}";
        var payload = $$"""
            {
              "beatmap": {{map}},
              "{{section}}": { {{mods}} }
            }
            """;
        if (string.IsNullOrWhiteSpace(beatmapPath))
        {
            return payload;
        }

        var serializedPath = System.Text.Json.JsonSerializer.Serialize(beatmapPath);
        var hints = $@",
              ""files"": {{ ""beatmap"": {serializedPath} }},
              ""directPath"": {{ ""beatmapFile"": {serializedPath} }}
            }}";
        return payload[..payload.LastIndexOf('}')].TrimEnd() + hints;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<HttpResponseMessage> _responses;

        public RecordingHandler(params HttpResponseMessage[] responses) => _responses = new(responses);

        public List<string> RequestedPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestedPaths.Add(request.RequestUri!.AbsolutePath);
            if (!_responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException("The test response queue was exhausted.");
            }

            return Task.FromResult(response);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly CancellationToken _cancellationToken;

        public BlockingHandler(CancellationToken cancellationToken) => _cancellationToken = cancellationToken;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, _cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class RecordingDiagnostics : IAnalysisDiagnostics
    {
        public List<AnalysisDiagnostic> Entries { get; } = [];

        public void Report(AnalysisDiagnostic diagnostic) => Entries.Add(diagnostic);
    }
}
