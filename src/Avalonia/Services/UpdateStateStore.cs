using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

/// <summary>
/// Persists updater state without owning release lookup or installation policy.
/// Paths are injected so the store can be tested without touching the user's
/// real application data directory.
/// </summary>
public sealed class UpdateStateStore
{
    private readonly string _primaryPath;
    private readonly string _legacyPath;
    private readonly string _dataDirectory;
    private readonly JsonSerializerOptions _jsonOptions;

    public UpdateStateStore(
        string primaryPath,
        string legacyPath,
        string dataDirectory,
        JsonSerializerOptions? jsonOptions = null)
    {
        if (string.IsNullOrWhiteSpace(primaryPath))
        {
            throw new ArgumentException("A primary state path is required.", nameof(primaryPath));
        }

        if (string.IsNullOrWhiteSpace(legacyPath))
        {
            throw new ArgumentException("A legacy state path is required.", nameof(legacyPath));
        }

        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
        }

        _primaryPath = primaryPath;
        _legacyPath = legacyPath;
        _dataDirectory = dataDirectory;
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    public async Task<InstallState> LoadAsync(CancellationToken cancellationToken = default)
    {
        foreach (var path in new[] { _primaryPath, _legacyPath })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                await using var stream = File.OpenRead(path);
                return await JsonSerializer.DeserializeAsync<InstallState>(
                    stream,
                    _jsonOptions,
                    cancellationToken).ConfigureAwait(false) ?? new InstallState();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                AppLogger.Error($"Loading install state '{path}'", exception);
            }
        }

        return new InstallState();
    }

    public async Task SaveAsync(InstallState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(_dataDirectory);
        string directory = Path.GetDirectoryName(_primaryPath) ?? _dataDirectory;
        Directory.CreateDirectory(directory);
        string temporaryPath = _primaryPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _primaryPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception)
            {
                AppLogger.Warning("Cleaning temporary install state", "The temporary state file could not be removed.", exception);
            }
        }
    }
}
