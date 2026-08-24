using System;
using System.IO;
using System.Text;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Application;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

/// <summary>
/// Atomic file transport used by Tosu's fullscreen static overlay document.
/// The sink is deliberately independent from AppPaths so its freshness and
/// recovery behaviour can be tested without a running Tosu instance.
/// </summary>
public sealed class FullscreenViewStateFileSink
{
    private const string ViewStateFileName = "view-state.json";
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly string _directory;

    public FullscreenViewStateFileSink(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A writable fullscreen transport directory is required.", nameof(directory));
        }

        _directory = directory;
    }

    public string ViewStatePath => Path.Combine(_directory, ViewStateFileName);

    public void Write(OverlayViewState viewState)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var target = ViewStatePath;
            var temporary = target + ".tmp";
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(viewState, _jsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, target, overwrite: true);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(ViewStatePath))
            {
                File.Delete(ViewStatePath);
            }

            var temporary = ViewStatePath + ".tmp";
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
