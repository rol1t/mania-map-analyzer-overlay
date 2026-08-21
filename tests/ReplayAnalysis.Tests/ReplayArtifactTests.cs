using System.Text.Json;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class ReplayArtifactTests
{
    [Fact]
    public void ArtifactHandleIsOpaqueAndNeverEmbedsBytesAsString()
    {
        byte[] bytes = [0x01, 0x02, 0x03];
        var store = new InMemoryReplayArtifactStore();
        ReplayArtifactHandle handle = store.Create(bytes, fileName: "play.osr");

        // Handle metadata is safe for logs/settings; raw bytes are not.
        string json = JsonSerializer.Serialize(new
        {
            handle.ArtifactId,
            handle.FileName,
            handle.ByteLength,
            handle.ContentHash
        });

        Assert.DoesNotContain(Convert.ToBase64String(bytes), json);
        Assert.Equal(3, handle.ByteLength);
        Assert.False(string.IsNullOrWhiteSpace(handle.ContentHash));

        // Bytes are reachable only via store.
        ReadOnlyMemory<byte> memory = store.GetBytes(handle);
        Assert.Equal(bytes, memory.ToArray());

        // Mutating original array does not affect stored bytes.
        bytes[0] = 0xFF;
        Assert.Equal(0x01, store.GetBytes(handle).Span[0]);
    }

    [Fact]
    public void ArtifactRequiresHandle()
    {
        Assert.Throws<ArgumentNullException>(() => new ReplayArtifact(null!, ReplaySourceKind.StableOsr));
    }

    [Fact]
    public void StoreReturnsBytesOnlyThroughHandle()
    {
        var store = new InMemoryReplayArtifactStore();
        var handle = store.Create([0x10, 0x20]);
        var artifact = new ReplayArtifact(handle, ReplaySourceKind.StableOsr, playerName: "Player");

        // Artifact itself carries no bytes; diagnostics/logs may reference only the handle id.
        Assert.Equal(handle.ArtifactId, artifact.Handle.ArtifactId);
        Assert.True(store.TryGetBytes(handle, out ReadOnlyMemory<byte> bytes));
        Assert.Equal(2, bytes.Length);
    }
}
