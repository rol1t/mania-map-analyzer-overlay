using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

public sealed partial class AnalyzerEngineScriptBridge
{
    private void ScriptHost_MessageReceived(object? sender, AnalyzerScriptMessageEventArgs e)
    {
        try
        {
            if (!e.Body.StartsWith(NativeMessagePrefix, StringComparison.Ordinal))
            {
                return;
            }

            var json = e.Body[NativeMessagePrefix.Length..];
            using var document = JsonDocument.Parse(json);
            HandleProtocolMessage(document.RootElement);
        }
        catch (JsonException exception)
        {
            var diagnostic = AnalysisDiagnostic.Error(
                "engine.message_invalid_json",
                $"Analyzer engine '{Descriptor.Id}' sent invalid JSON through the native bridge.",
                exception);
            Report(diagnostic, exception);
        }
        catch (Exception exception)
        {
            var diagnostic = AnalysisDiagnostic.Error(
                "engine.message_handling_failed",
                $"Analyzer engine '{Descriptor.Id}' sent a message that could not be handled.",
                exception);
            Report(diagnostic, exception);
        }
    }

    private void HandleProtocolMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Analyzer engine bridge messages must be JSON objects.");
        }

        var type = GetString(message, "type") ?? string.Empty;
        var protocol = GetString(message, "protocol");
        if (!string.Equals(protocol, _protocol, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Analyzer engine bridge message has an unsupported protocol.");
        }

        if (message.TryGetProperty("protocolVersion", out var protocolVersion) &&
            protocolVersion.ValueKind == JsonValueKind.Number &&
            protocolVersion.TryGetInt32(out var version) &&
            version != _protocolVersion)
        {
            throw new InvalidDataException("Analyzer engine bridge message has an unsupported protocol version.");
        }

        var messageEngineId = GetString(message, "engineId");
        if (!string.IsNullOrWhiteSpace(messageEngineId) &&
            !string.Equals(messageEngineId, Descriptor.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var messageSessionId = GetString(message, "sessionId");
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(messageSessionId) &&
                !string.Equals(messageSessionId, _activeSessionId, StringComparison.Ordinal))
            {
                return;
            }
        }

        if (!string.Equals(type, "runtime.ready", StringComparison.Ordinal) &&
            !string.Equals(type, "analysis.result", StringComparison.Ordinal) &&
            !string.Equals(type, "analysis.error", StringComparison.Ordinal) &&
            !string.Equals(type, "analysis.cancelled", StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(type, "runtime.ready", StringComparison.Ordinal))
        {
            HandleReadyMessage(message);
            return;
        }

        var correlationId = GetString(message, "correlationId");
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            var diagnostic = AnalysisDiagnostic.Error(
                "engine.response_correlation_missing",
                $"Analyzer engine '{Descriptor.Id}' returned a response without a correlation id.");
            Report(diagnostic);
            return;
        }

        PendingAnalysis? pending;
        lock (_sync)
        {
            _pending.TryGetValue(correlationId, out pending);
            if (pending is not null)
            {
                _pending.Remove(correlationId);
            }
        }

        if (pending is null)
        {
            // A response arriving after cancellation/reset is expected. It is
            // deliberately ignored so a stale worker cannot affect a new one.
            return;
        }

        var result = MapResponse(message, type, pending.Request);
        pending.Completion.TrySetResult(result);
    }

    private void HandleReadyMessage(JsonElement message)
    {
        var status = GetString(message, "status");
        var diagnostics = MapDiagnostics(message, "diagnostics").ToList();
        if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
        {
            var error = MapError(message);
            foreach (var diagnostic in diagnostics)
            {
                Report(diagnostic);
            }

            var failure = error ?? AnalysisDiagnostic.Error(
                "engine.runtime_incompatible",
                $"Analyzer engine '{Descriptor.Id}' reported an incompatible runtime.");
            Report(failure);
            lock (_sync)
            {
                _initializationTask = null;
            }

            _readySource?.TrySetException(
                new AnalyzerEngineBridgeException(failure.Message, null, failure));
            return;
        }

        var ready = new AnalyzerEngineBridgeReady(
            string.Equals(status, "partial", StringComparison.OrdinalIgnoreCase),
            diagnostics);
        foreach (var diagnostic in diagnostics)
        {
            Report(diagnostic);
        }

        _readySource?.TrySetResult(ready);
    }
}
