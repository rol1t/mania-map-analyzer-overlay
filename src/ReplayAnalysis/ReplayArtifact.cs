using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

/// <summary>
/// Immutable, opaque replay source. Binary bytes never leave this type as a
/// base64 string, settings value, log payload, or WebView message. Hosts must
/// use <see cref="ReplayArtifactHandle"/> and <see cref="IReplayArtifactStore"/>.
/// </summary>
public sealed record ReplayArtifact
{
    public ReplayArtifact(
        ReplayArtifactHandle handle,
        ReplaySourceKind sourceKind,
        string? playerName = null,
        string? mods = null,
        string? clientVersion = null,
        double? clockRate = null,
        ImmutableDictionary<string, string>? properties = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        SourceKind = sourceKind;
        Handle = handle;
        PlayerName = playerName?.Trim() ?? string.Empty;
        Mods = mods?.Trim() ?? string.Empty;
        ClientVersion = clientVersion?.Trim() ?? string.Empty;
        ClockRate = clockRate;
        Properties = (properties ?? ImmutableDictionary<string, string>.Empty)
            .ToImmutableDictionary(
                pair => pair.Key.Trim(),
                pair => pair.Value?.Trim() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }

    public ReplayArtifactHandle Handle
    {
        get;
    }

    public ReplaySourceKind SourceKind
    {
        get;
    }

    public string PlayerName
    {
        get;
    }

    public string Mods
    {
        get;
    }

    public string ClientVersion
    {
        get;
    }

    public double? ClockRate
    {
        get;
    }

    public ImmutableDictionary<string, string> Properties
    {
        get;
    }
}

/// <summary>
/// Opaque byte handle for a replay. The underlying bytes are held by an
/// <see cref="IReplayArtifactStore"/> and accessed only through this handle.
/// No serialization may embed raw bytes as base64 or inline strings.
/// </summary>
public sealed class ReplayArtifactHandle
{
    private readonly byte[] _bytes;

    public ReplayArtifactHandle(
        byte[] bytes,
        string artifactId,
        string? fileName = null,
        string? contentHash = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (string.IsNullOrWhiteSpace(artifactId))
        {
            throw new ArgumentException("An artifact id is required.", nameof(artifactId));
        }

        _bytes = (byte[])bytes.Clone();
        ArtifactId = artifactId.Trim();
        FileName = fileName?.Trim() ?? string.Empty;
        ContentHash = contentHash?.Trim() ?? string.Empty;
        ByteLength = _bytes.Length;
    }

    public string ArtifactId
    {
        get;
    }

    public string FileName
    {
        get;
    }

    public string ContentHash
    {
        get;
    }

    public int ByteLength
    {
        get;
    }

    internal byte[] CloneBytes()
    {
        return (byte[])_bytes.Clone();
    }

    internal ReadOnlyMemory<byte> AsMemory()
    {
        return _bytes;
    }
}

public interface IReplayArtifactStore
{
    ReplayArtifactHandle Create(byte[] bytes, string? fileName = null);

    ReadOnlyMemory<byte> GetBytes(ReplayArtifactHandle handle);

    bool TryGetBytes(ReplayArtifactHandle handle, out ReadOnlyMemory<byte> bytes);
}

public sealed class InMemoryReplayArtifactStore : IReplayArtifactStore
{
    private readonly Dictionary<string, byte[]> _storage = new(StringComparer.Ordinal);

    public ReplayArtifactHandle Create(byte[] bytes, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        string artifactId = Guid.NewGuid().ToString("N");
        string hash = ComputeHash(bytes);
        _storage[artifactId] = (byte[])bytes.Clone();
        return new ReplayArtifactHandle(bytes, artifactId, fileName, hash);
    }

    public ReadOnlyMemory<byte> GetBytes(ReplayArtifactHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!_storage.TryGetValue(handle.ArtifactId, out byte[]? stored))
        {
            throw new KeyNotFoundException($"Replay artifact '{handle.ArtifactId}' was not found in the artifact store.");
        }

        return stored;
    }

    public bool TryGetBytes(ReplayArtifactHandle handle, out ReadOnlyMemory<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (_storage.TryGetValue(handle.ArtifactId, out byte[]? stored))
        {
            bytes = stored;
            return true;
        }

        bytes = ReadOnlyMemory<byte>.Empty;
        return false;
    }

    private static string ComputeHash(byte[] bytes)
    {
        using System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
