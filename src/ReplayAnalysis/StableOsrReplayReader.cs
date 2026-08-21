using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using SharpCompress.Compressors.LZMA;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public sealed record StableOsrMetadata
{
    public int Mode
    {
        get; init;
    }
    public int ClientVersion
    {
        get; init;
    }
    public string BeatmapHash { get; init; } = string.Empty;
    public string PlayerName { get; init; } = string.Empty;
    public string ReplayHash { get; init; } = string.Empty;
    public int Score
    {
        get; init;
    }
    public int MaxCombo
    {
        get; init;
    }
    public bool Perfect
    {
        get; init;
    }
    public int Mods
    {
        get; init;
    }
    public DateTimeOffset? PlayedAt
    {
        get; init;
    }
    public long OnlineScoreId
    {
        get; init;
    }
    public int PerfectCount
    {
        get; init;
    }
    public int GreatCount
    {
        get; init;
    }
    public int GoodCount
    {
        get; init;
    }
    public int OkCount
    {
        get; init;
    }
    public int MehCount
    {
        get; init;
    }
    public int MissCount
    {
        get; init;
    }
}

public sealed record StableOsrReplay(
    StableOsrMetadata Metadata,
    string FrameData,
    ImmutableArray<ReplayInputEvent> InputEvents);

/// <summary>
/// Reads the stable .osr container from an opaque artifact byte span. Filesystem
/// access remains in the host; this parser only receives bytes from the artifact store.
/// The replay payload is an LZMA-alone stream followed by osu! frame text:
/// delta|x|y|keys, ... .
/// </summary>
public static class StableOsrReplayReader
{
    private const byte OsuStringMarker = 0x0B;
    private const int LzmaHeaderLength = 13;

    public static StableOsrReplay Read(ReadOnlyMemory<byte> artifactBytes)
    {
        if (artifactBytes.IsEmpty)
        {
            throw new ReplayCorruptException("The .osr artifact is empty.");
        }

        try
        {
            using var stream = new MemoryStream(artifactBytes.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            int mode = reader.ReadByte();
            int version = reader.ReadInt32();
            string beatmapHash = ReadOsuString(reader);
            string playerName = ReadOsuString(reader);
            string replayHash = ReadOsuString(reader);

            int perfectCount = reader.ReadUInt16();
            int greatCount = reader.ReadUInt16();
            int goodCount = reader.ReadUInt16();
            int okCount = reader.ReadUInt16();
            int mehCount = reader.ReadUInt16();
            int missCount = reader.ReadUInt16();
            int score = reader.ReadInt32();
            int maxCombo = reader.ReadUInt16();
            bool perfect = reader.ReadByte() != 0;
            int mods = reader.ReadInt32();
            _ = ReadOsuString(reader); // life-bar graph, not needed for input reconstruction
            long ticks = reader.ReadInt64();
            int compressedLength = reader.ReadInt32();

            if (compressedLength < 0 || compressedLength > stream.Length - stream.Position)
            {
                throw new ReplayCorruptException("The .osr replay payload length is invalid.");
            }

            byte[] compressed = reader.ReadBytes(compressedLength);
            if (compressed.Length != compressedLength)
            {
                throw new ReplayCorruptException("The .osr replay payload is truncated.");
            }

            long onlineScoreId = stream.Position + sizeof(long) <= stream.Length
                ? reader.ReadInt64()
                : 0;
            string frameData = DecompressFrameData(compressed);
            IReadOnlyList<ReplayInputEvent> inputEvents = StableReplayDecoder.DecodeFrameString(
                frameData,
                sourcePrecision: "stable.osr.frames");

            DateTimeOffset? playedAt = ticks > 0
                ? new DateTimeOffset(ticks, TimeSpan.Zero)
                : null;
            var metadata = new StableOsrMetadata
            {
                Mode = mode,
                ClientVersion = version,
                BeatmapHash = beatmapHash,
                PlayerName = playerName,
                ReplayHash = replayHash,
                Score = score,
                MaxCombo = maxCombo,
                Perfect = perfect,
                Mods = mods,
                PlayedAt = playedAt,
                OnlineScoreId = onlineScoreId,
                PerfectCount = perfectCount,
                GreatCount = greatCount,
                GoodCount = goodCount,
                OkCount = okCount,
                MehCount = mehCount,
                MissCount = missCount
            };

            return new StableOsrReplay(metadata, frameData, inputEvents.ToImmutableArray());
        }
        catch (ReplayAnalysisException)
        {
            throw;
        }
        catch (EndOfStreamException exception)
        {
            throw new ReplayCorruptException("The .osr header is truncated.", exception);
        }
        catch (InvalidDataException exception)
        {
            throw new ReplayCorruptException("The .osr LZMA payload is invalid.", exception);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or OverflowException)
        {
            throw new ReplayCorruptException("The .osr artifact could not be decoded.", exception);
        }
    }

    private static string DecompressFrameData(byte[] compressed)
    {
        if (compressed.Length < LzmaHeaderLength)
        {
            throw new ReplayCorruptException("The .osr LZMA payload is shorter than its header.");
        }

        byte[] properties = compressed[..5];
        ulong decodedLength = BinaryPrimitives.ReadUInt64LittleEndian(compressed.AsSpan(5, 8));
        if (decodedLength > int.MaxValue)
        {
            throw new ReplayCorruptException("The .osr replay frame payload is too large.");
        }

        using var compressedStream = new MemoryStream(
            compressed,
            LzmaHeaderLength,
            compressed.Length - LzmaHeaderLength,
            writable: false);
        using var decoder = LzmaStream.Create(
            properties,
            compressedStream,
            compressed.Length - LzmaHeaderLength,
            (long)decodedLength,
            leaveOpen: false);
        using var output = new MemoryStream((int)decodedLength);
        decoder.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static string ReadOsuString(BinaryReader reader)
    {
        byte marker = reader.ReadByte();
        if (marker == 0)
        {
            return string.Empty;
        }

        if (marker != OsuStringMarker)
        {
            throw new InvalidDataException($"Unexpected osu! string marker 0x{marker:X2}.");
        }

        int byteLength = Read7BitEncodedInt(reader);
        if (byteLength < 0 || byteLength > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("The osu! string length is invalid.");
        }

        return Encoding.UTF8.GetString(reader.ReadBytes(byteLength));
    }

    private static int Read7BitEncodedInt(BinaryReader reader)
    {
        int value = 0;
        int shift = 0;
        while (shift < 35)
        {
            byte current = reader.ReadByte();
            value |= (current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }

        throw new InvalidDataException("The osu! string length encoding is invalid.");
    }
}
