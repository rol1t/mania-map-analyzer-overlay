namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public static class OsuBeatmapParser
{
    public static OsuManiaBeatmap Parse(string osuContent, string beatmapHash)
    {
        if (string.IsNullOrWhiteSpace(osuContent))
        {
            throw new ReplayCorruptException("osu content is empty.");
        }

        if (string.IsNullOrWhiteSpace(beatmapHash))
        {
            throw new ArgumentException("A beatmap hash is required.", nameof(beatmapHash));
        }

        int keyCount = 4;
        bool inDifficulty = false;
        bool inHitObjects = false;
        List<OsuManiaHitObject> objects = [];
        int objectIndex = 0;

        string[] lines = osuContent.Split(["\r\n", "\n"], StringSplitOptions.None);

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inDifficulty = string.Equals(line, "[Difficulty]", StringComparison.OrdinalIgnoreCase);
                inHitObjects = string.Equals(line, "[HitObjects]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inDifficulty && line.StartsWith("CircleSize:", StringComparison.OrdinalIgnoreCase))
            {
                string value = line["CircleSize:".Length..].Trim();
                if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                {
                    keyCount = (int)Math.Round(parsed);
                }
            }

            if (inHitObjects)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                OsuManiaHitObject? hitObject = TryParseHitObject(line, keyCount, objectIndex);
                if (hitObject is not null)
                {
                    objects.Add(hitObject);
                    objectIndex++;
                }
            }
        }

        try
        {
            return new OsuManiaBeatmap(beatmapHash, keyCount, objects);
        }
        catch (Exception exception) when (exception is not ReplayAnalysisException)
        {
            throw new ReplayCorruptException($"Failed to build beatmap '{beatmapHash}': {exception.Message}", exception);
        }
    }

    private static OsuManiaHitObject? TryParseHitObject(string line, int keyCount, int objectIndex)
    {
        string[] parts = line.Split(',');
        if (parts.Length < 4)
        {
            return null;
        }

        if (!int.TryParse(parts[0].Trim(), out int x))
        {
            return null;
        }

        if (!int.TryParse(parts[2].Trim(), out int time))
        {
            return null;
        }

        if (!int.TryParse(parts[3].Trim(), out int type))
        {
            return null;
        }

        int column = ClampColumnFromX(x, keyCount);
        bool isLongNote = (type & 128) != 0;

        int? endTime = null;
        if (isLongNote && parts.Length >= 6)
        {
            string hitSample = parts[5].Trim();
            string endTimePart = hitSample.Split(':')[0];
            if (int.TryParse(endTimePart, out int parsedEnd))
            {
                endTime = parsedEnd;
            }
        }

        string id = $"obj-{objectIndex:D6}-{time}-{column}";
        return new OsuManiaHitObject(id, time, column, isLongNote, endTime);
    }

    private static int ClampColumnFromX(int x, int keyCount)
    {
        int clamped = Math.Clamp(x, 0, 512);
        int column = (int)Math.Floor((double)clamped * keyCount / 512.0);
        return Math.Clamp(column, 0, keyCount - 1);
    }
}
