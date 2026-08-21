using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Fully describes one headless analysis operation. <see cref="ProfileId"/>
/// records visual preset provenance and is deliberately excluded from the
/// compute key. Only normalized analyzer configuration affects de-duplication.
/// </summary>
public sealed record AnalysisRequest
{
    /// <summary>
    /// Normal play speed used when a source does not request a rate change.
    /// </summary>
    public const double DefaultRate = 1.0;

    /// <summary>
    /// Reserved transport option derived from <see cref="Rate"/> by analyzer
    /// hosts. Presets must use <see cref="Rate"/> instead of defining this
    /// option independently.
    /// </summary>
    public const string ReservedSpeedRateOptionName = "speedRate";

    public AnalysisRequest(
        string engineId,
        BeatmapIdentity beatmap,
        string beatmapContent,
        AnalysisConfiguration configuration,
        string profileId,
        double rate = DefaultRate,
        IEnumerable<string>? mods = null)
    {
        if (string.IsNullOrWhiteSpace(engineId))
        {
            throw new ArgumentException("An analyzer engine id is required.", nameof(engineId));
        }

        ArgumentNullException.ThrowIfNull(beatmap);
        ArgumentNullException.ThrowIfNull(configuration);

        if (beatmapContent is null)
        {
            throw new ArgumentNullException(nameof(beatmapContent));
        }

        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("An analysis profile id is required.", nameof(profileId));
        }

        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "The rate must be a finite positive number.");
        }

        EngineId = engineId.Trim();
        Beatmap = beatmap;
        BeatmapContent = beatmapContent;
        Configuration = configuration;
        ProfileId = profileId.Trim();
        Rate = rate;
        Mods = NormalizeMods(mods);
        BeatmapContentHash = GetContentHash(BeatmapContent);
        Key = AnalysisRequestKey.Create(this);
    }

    public AnalysisRequest(
        string engineId,
        BeatmapIdentity beatmap,
        string beatmapContent,
        string requestedAlgorithm,
        string profileId,
        double rate = DefaultRate,
        IEnumerable<string>? mods = null,
        IEnumerable<KeyValuePair<string, JsonElement>>? options = null,
        string configurationVersion = "1")
        : this(
            engineId,
            beatmap,
            beatmapContent,
            new AnalysisConfiguration(requestedAlgorithm, configurationVersion, options),
            profileId,
            rate,
            mods)
    {
    }

    public string EngineId
    {
        get;
    }

    public BeatmapIdentity Beatmap
    {
        get;
    }

    public string BeatmapContent
    {
        get;
    }

    public string BeatmapContentHash
    {
        get;
    }

    public AnalysisConfiguration Configuration
    {
        get;
    }

    public string RequestedAlgorithm => Configuration.RequestedAlgorithm;

    public ImmutableDictionary<string, JsonElement> Options => Configuration.Options;

    public string ProfileId
    {
        get;
    }

    public double Rate
    {
        get;
    }

    public ImmutableArray<string> Mods
    {
        get;
    }

    public AnalysisRequestKey Key
    {
        get;
    }

    private static ImmutableArray<string> NormalizeMods(IEnumerable<string>? mods)
    {
        return (mods ?? Array.Empty<string>())
            .Where(mod => !string.IsNullOrWhiteSpace(mod))
            .Select(mod => mod.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(mod => mod, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    internal static string GetContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    internal static string FormatRate(double rate)
    {
        return rate.ToString("G17", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Stable, content-addressed compute key. Engine and upstream implementation
/// versions are added by <see cref="AnalyzerExecutionPlanner"/>.
/// </summary>
public readonly record struct AnalysisRequestKey(string Value)
{
    public override string ToString() => Value;

    internal static AnalysisRequestKey Create(AnalysisRequest request)
    {
        var builder = new StringBuilder();
        builder.Append(request.EngineId.ToUpperInvariant());
        builder.Append('\n');
        builder.Append(request.Beatmap.StableKey);
        builder.Append('\n');
        builder.Append(request.BeatmapContentHash);
        builder.Append('\n');
        builder.Append(request.Configuration.GetStableIdentity());
        builder.Append(AnalysisRequest.FormatRate(request.Rate));
        builder.Append('\n');
        builder.Append(string.Join(',', request.Mods));
        builder.Append('\n');

        var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return new AnalysisRequestKey(Convert.ToHexString(keyBytes));
    }
}
