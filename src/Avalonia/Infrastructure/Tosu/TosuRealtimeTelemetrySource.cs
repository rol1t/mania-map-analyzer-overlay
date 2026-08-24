using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;

/// <summary>
/// Adapts one raw Tosu payload transport to the shared realtime collector.
/// The delegate is transport-only so HTTP polling and a future WebSocket
/// adapter cannot grow separate normalization rules.
/// </summary>
public sealed class TosuRealtimeTelemetrySource : IRealtimeTelemetrySource
{
    private readonly Func<CancellationToken, Task<JsonElement?>> _readPayload;
    private readonly TosuRealtimeCollector _collector;
    private readonly string _source;

    public TosuRealtimeTelemetrySource(
        Func<CancellationToken, Task<JsonElement?>> readPayload,
        string source = "native-http",
        PauseCoachOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(readPayload);
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("A realtime source name is required.", nameof(source));
        }

        _readPayload = readPayload;
        _collector = new TosuRealtimeCollector(options);
        _source = source;
    }

    public async Task<TosuRealtimeTelemetry?> ReadAsync(CancellationToken cancellationToken = default)
    {
        JsonElement? payload = await _readPayload(cancellationToken).ConfigureAwait(false);
        return payload is JsonElement value
            ? _collector.Process(value, _source)
            : null;
    }

    public void Reset() => _collector.Reset();
}
