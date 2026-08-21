namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public sealed record OsuManiaHitObject
{
    public OsuManiaHitObject(string id, int startTimeMs, int column, bool isLongNote, int? endTimeMs = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A hit object id is required.", nameof(id));
        }

        if (column < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(column), "Column cannot be negative.");
        }

        if (isLongNote && endTimeMs is null)
        {
            throw new ArgumentException("LN end time is required for long notes.", nameof(endTimeMs));
        }

        if (endTimeMs is not null && endTimeMs.Value <= startTimeMs)
        {
            throw new ArgumentException("LN end time must be greater than start time.", nameof(endTimeMs));
        }

        Id = id.Trim();
        StartTimeMs = startTimeMs;
        Column = column;
        IsLongNote = isLongNote;
        EndTimeMs = endTimeMs;
        Phase = ReplayHitPhase.Note;
    }

    public string Id
    {
        get;
    }

    public int StartTimeMs
    {
        get;
    }

    public int Column
    {
        get;
    }

    public bool IsLongNote
    {
        get;
    }

    public int? EndTimeMs
    {
        get;
    }

    public ReplayHitPhase Phase
    {
        get;
    }
}
