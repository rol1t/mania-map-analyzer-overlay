using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;

/// <summary>
/// Obtains raw beatmap input from tosu's HTTP API. It reads the v2 identity
/// before and after downloading the .osu file, retrying when the game changes
/// maps during the request.
/// </summary>
public sealed class TosuBeatmapSource : ITosuBeatmapSource
{
    private const string JsonV2Route = "json/v2";
    private const string BeatmapFileRoute = "files/beatmap/file";
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly IAnalysisDiagnostics _diagnostics;
    private readonly int _maxConsistencyAttempts;

    public TosuBeatmapSource(
        HttpClient httpClient,
        Uri baseUri,
        IAnalysisDiagnostics? diagnostics = null,
        int maxConsistencyAttempts = 2)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(baseUri);

        if (!baseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The tosu base URI must be absolute.", nameof(baseUri));
        }

        if (maxConsistencyAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConsistencyAttempts),
                maxConsistencyAttempts,
                "At least one consistency attempt is required.");
        }

        _httpClient = httpClient;
        _baseUri = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        _diagnostics = diagnostics ?? new AppLoggerAnalysisDiagnostics();
        _maxConsistencyAttempts = maxConsistencyAttempts;
    }

    public async Task<TosuBeatmapSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= _maxConsistencyAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var before = await ReadPayloadAsync(cancellationToken);
                var identityBefore = ExtractIdentity(before.Root);
                var rawBeatmap = await ReadBeatmapFileAsync(before.Root, cancellationToken);
                var after = await ReadPayloadAsync(cancellationToken);
                var identityAfter = ExtractIdentity(after.Root);

                if (!IsConsistentIdentity(identityBefore, identityAfter))
                {
                    if (attempt < _maxConsistencyAttempts)
                    {
                        ReportWarning(
                            "tosu.beatmap_changed_during_fetch",
                            $"The beatmap changed while it was being fetched; retrying ({attempt}/{_maxConsistencyAttempts}).",
                            new Dictionary<string, string>
                            {
                                ["before"] = identityBefore.StableKey,
                                ["after"] = identityAfter.StableKey,
                                ["attempt"] = attempt.ToString(CultureInfo.InvariantCulture)
                            });
                        continue;
                    }

                    throw new TosuBeatmapSourceException(
                        $"The beatmap changed during all {_maxConsistencyAttempts} fetch attempts.",
                        new InvalidOperationException("Tosu returned inconsistent beatmap identities."),
                        TosuBeatmapSourceFailureKind.BeatmapChanged);
                }

                // The file belongs to the same beatmap identity, but selected
                // mods/rate may change while it is being downloaded. Use the
                // newest consistent payload so a mod click is not lost by a
                // stale `before` frame.
                return CreateSnapshot(identityAfter, after.Root, rawBeatmap);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TosuBeatmapSourceException exception) when (IsOsuNotRunningException(exception))
            {
                ReportOsuNotRunning(exception);
                throw;
            }
            catch (TosuBeatmapSourceException exception) when (IsNoBeatmapException(exception))
            {
                ReportNoBeatmap(exception);
                throw;
            }
            catch (TosuBeatmapSourceException exception)
            {
                ReportError("Reading current beatmap from tosu", exception);
                throw;
            }
            catch (Exception exception)
            {
                var wrapped = new TosuBeatmapSourceException(
                    "tosu did not provide a usable beatmap snapshot.",
                    exception);
                if (IsOsuNotRunningException(wrapped))
                {
                    ReportOsuNotRunning(wrapped);
                }
                else if (IsNoBeatmapException(wrapped) || IsNoBeatmapException(exception))
                {
                    ReportNoBeatmap(wrapped);
                }
                else
                {
                    ReportError("Reading current beatmap from tosu", wrapped);
                }

                throw wrapped;
            }
        }

        throw new InvalidOperationException("The tosu beatmap fetch loop completed unexpectedly.");
    }

    private async Task<TosuV2Payload> ReadPayloadAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(JsonV2Route, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(content);
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new TosuBeatmapSourceException(
                    "tosu returned a JSON payload that is not an object.",
                    new InvalidDataException("Tosu JSON root is not an object."),
                    TosuBeatmapSourceFailureKind.MalformedPayload);
            }

            return new TosuV2Payload(root);
        }
        catch (JsonException exception)
        {
            throw new TosuBeatmapSourceException(
                "tosu returned malformed JSON from /json/v2.",
                exception,
                TosuBeatmapSourceFailureKind.MalformedPayload);
        }
    }

    private async Task<string> ReadBeatmapFileAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadTextAsync(BeatmapFileRoute, cancellationToken);
        }
        catch (TosuBeatmapSourceException exception) when (IsNotFound(exception, BeatmapFileRoute))
        {
            // The shortcut depends on tosu having a populated menu folder and
            // filename. During lazer/stable transitions those fields can be
            // temporarily empty even though /json/v2 already exposes the
            // beatmap path. Try the path-based route before reporting a
            // missing current map.
            foreach (var route in ExtractBeatmapFileRoutes(payload))
            {
                try
                {
                    var content = await ReadTextAsync(route, cancellationToken);
                    ReportWarning(
                        "tosu.beatmap_file_fallback",
                        "The current beatmap shortcut was unavailable; loaded the beatmap through its path.",
                        new Dictionary<string, string>
                        {
                            ["shortcut"] = BeatmapFileRoute,
                            ["route"] = route
                        });
                    return content;
                }
                catch (TosuBeatmapSourceException fallbackException) when (IsNotFound(fallbackException, route))
                {
                    // A stale path hint is possible while the selected map is
                    // changing. Try the next hint, then rethrow the original
                    // shortcut 404 so the caller can retry on the next poll.
                }
            }

            throw;
        }
    }

    private async Task<string> ReadTextAsync(string route, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(route, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            var message = string.Equals(route, BeatmapFileRoute, StringComparison.OrdinalIgnoreCase)
                ? "tosu returned an empty beatmap file."
                : $"tosu returned an empty response from '{route}'.";
            throw new TosuBeatmapSourceException(
                message,
                new InvalidDataException("Tosu returned an empty response."),
                TosuBeatmapSourceFailureKind.EmptyResponse);
        }

        return content;
    }

    private static IEnumerable<string> ExtractBeatmapFileRoutes(JsonElement payload)
    {
        var candidates = new List<string>();
        AddPathCandidate(candidates, payload, "directPath", "beatmapFile");
        AddPathCandidate(candidates, payload, "files", "beatmap");

        if (TryGetObject(payload, out var menu, "menu"))
        {
            var folder = ReadString(menu, "folder", "path");
            var filename = ReadString(menu, "filename", "fileName", "beatmapFile");
            if (!string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(filename))
            {
                AddPathCandidate(candidates, $"{folder.TrimEnd('\\', '/')}/{filename}");
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalized = candidate.Trim().Replace('\\', '/');
            while (normalized.StartsWith('/'))
            {
                normalized = normalized[1..];
            }

            if (normalized.StartsWith("files/beatmap/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized["files/beatmap/".Length..];
            }
            else if (normalized.StartsWith("Songs/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized["Songs/".Length..];
            }

            var rawSegments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (rawSegments.Length == 0 ||
                rawSegments.Any(segment => segment is "." or ".." || segment.Contains(':')))
            {
                continue;
            }

            yield return "files/beatmap/" + string.Join('/', rawSegments.Select(Uri.EscapeDataString));
        }
    }

    private static void AddPathCandidate(List<string> candidates, JsonElement root, params string[] path)
    {
        if (TryGetPath(root, out var value, path) && value.ValueKind == JsonValueKind.String)
        {
            AddPathCandidate(candidates, value.GetString());
        }
    }

    private static void AddPathCandidate(List<string> candidates, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            candidates.Add(value.Trim());
        }
    }

    private static bool IsNotFound(TosuBeatmapSourceException exception, string route) =>
        exception.StatusCode == System.Net.HttpStatusCode.NotFound &&
        string.Equals(exception.Route, route, StringComparison.OrdinalIgnoreCase);

    private async Task<HttpResponseMessage> SendAsync(string route, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(new Uri(_baseUri, route), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.IO.IOException)
        {
            throw new TosuBeatmapSourceException(
                $"The tosu endpoint '{route}' could not be reached.",
                exception,
                TosuBeatmapSourceFailureKind.TransportUnavailable);
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = response.StatusCode;
            var status = ((int)statusCode).ToString(CultureInfo.InvariantCulture);
            var reason = response.ReasonPhrase;
            response.Dispose();
            throw new TosuBeatmapSourceException(
                $"The tosu endpoint '{route}' returned HTTP {status} ({reason}).",
                route,
                statusCode);
        }

        return response;
    }

    private static TosuBeatmapSnapshot CreateSnapshot(
        BeatmapIdentity identity,
        JsonElement root,
        string rawBeatmap)
    {
        var source = SelectMapObject(root);
        var metadata = new TosuBeatmapMetadata
        {
            Artist = ReadString(source, "artist"),
            Title = ReadString(source, "title"),
            Version = ReadString(source, "version"),
            Mapper = FirstNonEmpty(ReadString(source, "mapper"), ReadString(source, "creator")),
            // Current Tosu v2 packets expose calculated values under
            // beatmap.stats. Keep the older flat fields as compatibility
            // fallbacks, but prefer the full-map star value over `live`
            // (which can describe only the current map position).
            Bpm = ReadPositivePathNumber(source, "stats", "bpm", "common")
                ?? ReadPositivePathNumber(source, "stats", "bpm", "max")
                ?? ReadPositivePathNumber(source, "stats", "bpm", "min")
                ?? ReadPositivePathNumber(source, "stats", "bpm", "realtime")
                ?? ReadNumber(source, "bpm"),
            StarRating = ReadPositivePathNumber(source, "stats", "stars", "total")
                ?? ReadPositivePathNumber(source, "stats", "stars", "live")
                ?? ReadPositiveNumber(
                    source,
                    "star_rating",
                    "starRating",
                    "difficulty_rating",
                    "difficultyRating",
                    "sr"),
            OverallDifficulty = ReadStatNumber(source, "od")
                ?? ReadNumber(source, "overall_difficulty", "overallDifficulty", "od"),
            // Tosu v2 exposes the mania key count as
            // beatmap.stats.cs.converted/original. Older payloads used a flat
            // circle_size/cs value, so retain both shapes.
            CircleSize = ReadStatNumber(source, "cs")
                ?? ReadNumber(source, "circle_size", "circleSize", "cs"),
            ApproachRate = ReadStatNumber(source, "ar")
                ?? ReadNumber(source, "approach_rate", "approachRate", "ar"),
            HealthDrain = ReadStatNumber(source, "hp")
                ?? ReadNumber(source, "hp_drain", "hpDrain", "health_drain", "healthDrain", "hp"),
            Mode = FirstNonEmpty(ReadString(source, "mode"), ReadScalarString(source, "mode_int")),
            BackgroundPath = FirstNonEmpty(
                ReadString(source, "background"),
                ReadString(source, "background_url"),
                ReadString(source, "backgroundUrl"))
        };

        var mods = ExtractMods(root);
        var rate = ExtractRate(root, mods);
        return new TosuBeatmapSnapshot(
            identity,
            rawBeatmap,
            metadata,
            rate,
            mods,
            DateTimeOffset.UtcNow);
    }

    private static BeatmapIdentity ExtractIdentity(JsonElement root)
    {
        var source = SelectMapObject(root);
        var id = FirstNonEmpty(
            ReadScalarString(source, "id"),
            ReadScalarString(root, "beatmap_id"),
            ReadScalarString(root, "beatmapId"));
        var hash = FirstNonEmpty(
            ReadString(source, "md5"),
            ReadString(source, "checksum"),
            ReadString(source, "hash"));
        var setId = FirstNonEmpty(
            ReadScalarString(source, "set"),
            ReadScalarString(source, "set_id"),
            ReadScalarString(source, "setId"),
            ReadScalarString(source, "beatmapset_id"),
            ReadScalarString(source, "beatmapSetId"));

        try
        {
            return new BeatmapIdentity(id, hash, setId);
        }
        catch (ArgumentException exception)
        {
            throw new TosuBeatmapSourceException(
                "tosu returned a payload without a current beatmap identity.",
                exception,
                TosuBeatmapSourceFailureKind.NoBeatmap);
        }
    }

    private static bool IsConsistentIdentity(BeatmapIdentity before, BeatmapIdentity after)
    {
        var sharedDimension = false;
        if (!string.IsNullOrWhiteSpace(before.Id) && !string.IsNullOrWhiteSpace(after.Id))
        {
            sharedDimension = true;
            if (!string.Equals(before.Id, after.Id, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(before.Hash) && !string.IsNullOrWhiteSpace(after.Hash))
        {
            sharedDimension = true;
            if (!string.Equals(before.Hash, after.Hash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(before.SetId) && !string.IsNullOrWhiteSpace(after.SetId))
        {
            sharedDimension = true;
            if (!string.Equals(before.SetId, after.SetId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return sharedDimension;
    }

    private static JsonElement SelectMapObject(JsonElement root)
    {
        if (TryGetObject(root, out var beatmap, "beatmap") && HasIdentity(beatmap))
        {
            return beatmap;
        }

        if (TryGetObject(root, out var menu, "menu") && TryGetObject(menu, out var menuBeatmap, "bm"))
        {
            return menuBeatmap;
        }

        if (TryGetObject(root, out var play, "play") && TryGetObject(play, out var playBeatmap, "beatmap"))
        {
            return playBeatmap;
        }

        if (TryGetObject(root, out beatmap, "beatmap"))
        {
            return beatmap;
        }

        throw new TosuBeatmapSourceException(
            "tosu returned a payload without beatmap metadata.",
            new InvalidDataException("Tosu beatmap metadata is missing."),
            TosuBeatmapSourceFailureKind.NoBeatmap);
    }

    private static bool HasIdentity(JsonElement value) =>
        !string.IsNullOrWhiteSpace(ReadScalarString(value, "id")) ||
        !string.IsNullOrWhiteSpace(ReadString(value, "md5", "checksum", "hash"));

    private static ImmutableArray<string> ExtractMods(JsonElement root)
    {
        var candidates = new List<JsonElement>();
        var menuFirst = IsMenuState(root);
        var selectPlay = IsSelectPlayState(root);
        var resultsFirst = IsResultsState(root);
        if (menuFirst)
        {
            // Tosu v2 does not always include a `menu` object in selectPlay
            // packets. When it is absent, play.mods is the current selected
            // configuration rather than stale results data. An explicitly
            // present menu.mods (including an empty array) remains authoritative.
            // In lazer's selectPlay transition `menu.mods` can still be the
            // previous menu frame while `play.mods` already contains the
            // newly selected mod. Prefer the play payload for that state;
            // other menu states retain the menu-first precedence.
            if (selectPlay && !AddModsCandidate(root, candidates, "play"))
            {
                AddModsCandidate(root, candidates, "menu");
            }
            else if (!selectPlay && !AddModsCandidate(root, candidates, "menu"))
            {
                AddModsCandidate(root, candidates, "play");
            }
        }
        else if (resultsFirst)
        {
            AddModsCandidate(root, candidates, "resultsScreen");
            AddModsCandidate(root, candidates, "play");
        }
        else
        {
            // A live play object is authoritative over a stale results
            // screen. Do not reverse this ordering: Tosu keeps results data
            // around while a new attempt starts.
            AddModsCandidate(root, candidates, "play");
        }
        if (TryGetProperty(root, out var rootMods, "mods"))
        {
            candidates.Add(rootMods);
        }
        if (!menuFirst)
        {
            if (!resultsFirst)
            {
                AddModsCandidate(root, candidates, "resultsScreen");
            }
            AddModsCandidate(root, candidates, "menu");
        }

        foreach (var candidate in candidates)
        {
            // Presence of an empty mods array is meaningful: it says NM for
            // the current source and must not fall through to a previous
            // play/results object.
            var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectModCodes(candidate, mods);
            return mods.OrderBy(mod => mod, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        }

        return ImmutableArray<string>.Empty;
    }

    private static bool AddModsCandidate(
        JsonElement root,
        ICollection<JsonElement> candidates,
        string objectName)
    {
        if (TryGetObject(root, out var source, objectName) && TryGetProperty(source, out var mods, "mods"))
        {
            candidates.Add(mods);
            return true;
        }

        return false;
    }

    private static bool IsMenuState(JsonElement root)
    {
        if (!TryGetPath(root, out var state, "state") || state.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var name = ReadString(state, "name");
        var token = new string(name.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return token is "menu" or "select" or "songselect" or "selectplay" or "selectedit" or "edit" or "options" or "exit" or "lobby";
    }

    private static bool IsSelectPlayState(JsonElement root)
    {
        if (!TryGetPath(root, out var state, "state") || state.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var name = ReadString(state, "name");
        var token = new string(name.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return token is "selectplay" or "songselect";
    }

    private static bool IsResultsState(JsonElement root)
    {
        if (!TryGetPath(root, out var state, "state") || state.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var name = ReadString(state, "name");
        var token = new string(name.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return token is "results" or "result" or "resultscreen" or "ranking";
    }

    private static void CollectModCodes(JsonElement value, ISet<string> result)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                CollectModCodes(item, result);
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                AddModCodes(value.GetString() ?? string.Empty, result);
            }

            return;
        }

        if (TryGetProperty(value, out var array, "array", "list", "mods"))
        {
            // A structured mods container can expose both an aggregate name
            // (for example "NC CL") and the actual entries. The aggregate is
            // not a mod code; parsing it produced fake keys such as NCCL and
            // caused needless headless re-analysis.
            CollectModCodes(array, result);
            return;
        }

        foreach (var propertyName in new[] { "acronym", "str", "name" })
        {
            AddModCodes(ReadString(value, propertyName), result);
        }
    }

    private static void AddModCodes(string value, ISet<string> result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = new string(value
            .ToUpperInvariant()
            .Where(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            .ToArray());
        if (normalized is "NM" or "NOMOD" or "NONE")
        {
            return;
        }

        if (normalized.Length > 0)
        {
            result.Add(normalized);
        }
    }

    private static double ExtractRate(JsonElement root, ImmutableArray<string> mods)
    {
        var menuFirst = IsMenuState(root);
        var selectPlay = IsSelectPlayState(root);
        var resultsFirst = IsResultsState(root);
        bool playModsPresent = TryGetPath(root, out var playObject, "play")
            && TryGetProperty(playObject, out _, "mods");
        bool menuModsPresent = TryGetPath(root, out var menuObject, "menu")
            && TryGetProperty(menuObject, out _, "mods");
        var paths = selectPlay
            ? new[]
            {
                new[] { "play", "speedRate" },
                new[] { "play", "speed_rate" },
                new[] { "play", "rate" },
                new[] { "menu", "speedRate" },
                new[] { "menu", "speed_rate" },
                new[] { "menu", "rate" },
                new[] { "rate" },
                new[] { "game", "speedRate" },
                new[] { "game", "rate" }
            }
            : menuFirst
            ? new[]
            {
                new[] { "menu", "speedRate" },
                new[] { "menu", "speed_rate" },
                new[] { "menu", "rate" },
                new[] { "rate" },
                new[] { "game", "speedRate" },
                new[] { "game", "rate" }
            }
            : resultsFirst
                ? new[]
                {
                    new[] { "resultsScreen", "speedRate" },
                    new[] { "resultsScreen", "rate" },
                    new[] { "play", "speedRate" },
                    new[] { "play", "rate" },
                    new[] { "rate" }
                }
                : new[]
            {
                new[] { "play", "speedRate" },
                new[] { "play", "rate" },
                new[] { "game", "speedRate" },
                new[] { "game", "rate" },
                new[] { "menu", "speedRate" },
                new[] { "menu", "rate" },
                new[] { "rate" }
            };

        foreach (var path in paths)
        {
            // Once selectPlay exposes an explicit play.mods container, all
            // other top-level speed fields may belong to the previous
            // selection. Only direct play.* values can precede the structured
            // play.mods setting; this also protects an explicit NM selection
            // from stale root/game/menu rates.
            if (selectPlay && playModsPresent && path.Length > 0 &&
                !string.Equals(path[0], "play", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetPath(root, out var value, path) && TryReadNumber(value, out var rate) && rate > 0)
            {
                return rate;
            }
        }

        // In song select/edit, play.* can still describe the last played
        // attempt. SelectPlay is the exception: lazer can update play.mods
        // before menu.mods, so read that current selection first.
        if (selectPlay &&
            TryGetPath(root, out var selectPlayObject, "play") &&
            TryGetProperty(selectPlayObject, out var selectPlayMods, "mods"))
        {
            var selectPlaySettingRate = FindSpeedChange(selectPlayMods);
            if (selectPlaySettingRate > 0)
            {
                return selectPlaySettingRate;
            }
        }

        if (menuFirst && menuModsPresent && (!selectPlay || !playModsPresent) &&
            TryGetPath(root, out var menu, "menu") &&
            TryGetProperty(menu, out var menuMods, "mods"))
        {
            var menuSettingRate = FindSpeedChange(menuMods);
            if (menuSettingRate > 0)
            {
                return menuSettingRate;
            }
        }

        if (menuFirst && !menuModsPresent)
        {
            foreach (var path in new[]
            {
                new[] { "play", "speedRate" },
                new[] { "play", "speed_rate" },
                new[] { "play", "rate" }
            })
            {
                if (TryGetPath(root, out var value, path) && TryReadNumber(value, out var rate) && rate > 0)
                {
                    return rate;
                }
            }

            if (TryGetPath(root, out var play, "play") &&
                TryGetProperty(play, out var playMods, "mods"))
            {
                var playSettingRate = FindSpeedChange(playMods);
                if (playSettingRate > 0)
                {
                    return playSettingRate;
                }
            }
        }

        var inferredRate = InferRateFromMods(mods);
        if (menuFirst && inferredRate > 0)
        {
            return inferredRate;
        }

        var modObjects = selectPlay
            ? new[] { new[] { "play" }, new[] { "menu" } }
            : menuFirst
            ? new[] { new[] { "menu" } }
            : resultsFirst
                ? new[] { new[] { "resultsScreen" }, new[] { "play" }, new[] { "menu" } }
            : new[] { new[] { "play" }, new[] { "menu" } };
        foreach (var objectPath in modObjects)
        {
            if (selectPlay && playModsPresent && objectPath.Length > 0 &&
                string.Equals(objectPath[0], "menu", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetPath(root, out var source, objectPath) &&
                TryGetProperty(source, out var sourceMods, "mods"))
            {
                var settingRate = FindSpeedChange(sourceMods);
                if (settingRate > 0)
                {
                    return settingRate;
                }
            }
        }

        if ((!selectPlay || !playModsPresent) && TryGetProperty(root, out var rootMods, "mods"))
        {
            var settingRate = FindSpeedChange(rootMods);
            if (settingRate > 0)
            {
                return settingRate;
            }
        }

        return inferredRate > 0 ? inferredRate : 1.0;
    }

    private static double InferRateFromMods(ImmutableArray<string> mods)
    {
        if (mods.Contains("NC", StringComparer.OrdinalIgnoreCase) ||
            mods.Contains("DT", StringComparer.OrdinalIgnoreCase))
        {
            return 1.5;
        }

        if (mods.Contains("HT", StringComparer.OrdinalIgnoreCase) ||
            mods.Contains("DC", StringComparer.OrdinalIgnoreCase))
        {
            return 0.75;
        }

        return 0;
    }

    private static double FindSpeedChange(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var result = FindSpeedChange(item);
                if (result > 0)
                {
                    return result;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(value, out var directRate, "rate", "speedRate", "speed_rate") &&
                TryReadNumber(directRate, out var explicitRate) && explicitRate > 0)
            {
                return explicitRate;
            }

            if (TryGetProperty(value, out var settings, "settings") &&
                TryGetProperty(settings, out var speed, "speed_change", "speedChange") &&
                TryReadNumber(speed, out var rate) && rate > 0)
            {
                return rate;
            }

            if (TryGetProperty(value, out var nested, "array", "list", "mods"))
            {
                return FindSpeedChange(nested);
            }
        }

        return 0;
    }

    private static bool TryGetPath(JsonElement value, out JsonElement result, params string[] path)
    {
        result = value;
        foreach (var segment in path)
        {
            if (!TryGetProperty(result, out result, segment))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetObject(JsonElement value, out JsonElement result, params string[] names)
    {
        if (TryGetProperty(value, out result, names) && result.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryGetProperty(JsonElement value, out JsonElement result, params string[] names)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (value.TryGetProperty(name, out result))
                {
                    return true;
                }
            }
        }

        result = default;
        return false;
    }

    private static string ReadString(JsonElement value, params string[] names)
    {
        return TryGetProperty(value, out var property, names) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static string ReadScalarString(JsonElement value, params string[] names)
    {
        if (!TryGetProperty(value, out var property, names))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
    }

    private static double? ReadNumber(JsonElement value, params string[] names)
    {
        return TryGetProperty(value, out var property, names) && TryReadNumber(property, out var number)
            ? number
            : null;
    }

    private static double? ReadPositiveNumber(JsonElement value, params string[] names)
    {
        var number = ReadNumber(value, names);
        return number is > 0 && double.IsFinite(number.Value) ? number : null;
    }

    private static double? ReadPositivePathNumber(JsonElement value, params string[] path)
    {
        if (!TryGetPath(value, out var property, path)
            || !TryReadNumber(property, out var number)
            || number <= 0
            || !double.IsFinite(number))
        {
            return null;
        }

        return number;
    }

    private static double? ReadStatNumber(JsonElement beatmap, string statName)
    {
        if (!TryGetPath(beatmap, out var stat, "stats", statName))
        {
            return null;
        }

        if (TryReadNumber(stat, out var scalar))
        {
            return scalar;
        }

        // "converted" reflects active key-conversion mods when Tosu exposes
        // them; otherwise the original CircleSize is the chart key count.
        // Tosu commonly serializes an inactive key conversion as
        // converted=0. That is not a valid mania key count, so retain the
        // original CircleSize in that case.
        var converted = ReadNumber(stat, "converted");
        return converted is > 0 ? converted : ReadNumber(stat, "original");
    }

    private static bool TryReadNumber(JsonElement value, out double number)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return true;
        }

        number = 0;
        return false;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private void ReportWarning(string code, string message, IReadOnlyDictionary<string, string> properties)
    {
        _diagnostics.Report(new AnalysisDiagnostic(
            AnalysisDiagnosticSeverity.Warning,
            code,
            message,
            properties: properties));
    }

    private static bool IsOsuNotRunningException(Exception exception)
    {
        return FindTosuFailure(exception) == TosuBeatmapSourceFailureKind.OsuNotRunning;
    }

    private static bool IsNoBeatmapException(Exception exception)
    {
        if (FindTosuFailure(exception) == TosuBeatmapSourceFailureKind.NoBeatmap)
        {
            return true;
        }

        return exception is TosuBeatmapSourceException tosuException &&
               tosuException.StatusCode == System.Net.HttpStatusCode.NotFound &&
               string.Equals(tosuException.Route, BeatmapFileRoute, StringComparison.OrdinalIgnoreCase);
    }

    private static TosuBeatmapSourceFailureKind FindTosuFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TosuBeatmapSourceException typed &&
                typed.FailureKind != TosuBeatmapSourceFailureKind.Unknown)
            {
                return typed.FailureKind;
            }
        }

        return TosuBeatmapSourceFailureKind.Unknown;
    }

    private void ReportNoBeatmap(TosuBeatmapSourceException exception)
    {
        _diagnostics.Report(new AnalysisDiagnostic(
            AnalysisDiagnosticSeverity.Information,
            "tosu.no_beatmap",
            "No current beatmap is available (osu! is running but no map is selected).",
            properties: [new KeyValuePair<string, string>("reason", exception.Message)]));
    }

    private void ReportOsuNotRunning(TosuBeatmapSourceException exception)
    {
        _diagnostics.Report(new AnalysisDiagnostic(
            AnalysisDiagnosticSeverity.Information,
            "tosu.osu_not_running",
            "osu! client is not running (tosu returned HTTP 500).",
            properties: [new KeyValuePair<string, string>("reason", exception.Message)]));
    }

    private void ReportError(string operation, TosuBeatmapSourceException exception)
    {
        _diagnostics.Report(AnalysisDiagnostic.Error(
            "tosu.beatmap_source_failed",
            exception.Message,
            exception,
            [new KeyValuePair<string, string>("operation", operation)]));
    }

    private sealed record TosuV2Payload(JsonElement Root);

    private sealed class AppLoggerAnalysisDiagnostics : IAnalysisDiagnostics
    {
        public void Report(AnalysisDiagnostic diagnostic)
        {
            var message = diagnostic.Properties.Count == 0
                ? diagnostic.Message
                : diagnostic.Message + " [" + string.Join(", ", diagnostic.Properties.Select(property =>
                    property.Key + "=" + property.Value)) + "]";
            var exception = string.IsNullOrWhiteSpace(diagnostic.TechnicalDetails)
                ? null
                : new TosuBeatmapSourceException(diagnostic.TechnicalDetails);

            switch (diagnostic.Severity)
            {
                case AnalysisDiagnosticSeverity.Error:
                    AppLogger.Error(diagnostic.Code, exception ?? new TosuBeatmapSourceException(message));
                    break;
                case AnalysisDiagnosticSeverity.Warning:
                    AppLogger.Warning(diagnostic.Code, message, exception);
                    break;
                default:
                    AppLogger.Info(diagnostic.Code, message);
                    break;
            }
        }
    }
}
