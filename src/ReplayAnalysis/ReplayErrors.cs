namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public abstract class ReplayAnalysisException : Exception
{
    protected ReplayAnalysisException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code
    {
        get;
    }
}

public sealed class ReplayNotFoundException : ReplayAnalysisException
{
    public ReplayNotFoundException(string message)
        : base("replay.not_found", message)
    {
    }
}

public sealed class ReplayCorruptException : ReplayAnalysisException
{
    public ReplayCorruptException(string message, Exception? innerException = null)
        : base("replay.corrupt", message, innerException)
    {
    }
}

public sealed class ReplayBeatmapMismatchException : ReplayAnalysisException
{
    public ReplayBeatmapMismatchException(string message)
        : base("replay.beatmap_mismatch", message)
    {
    }

    public string? ExpectedBeatmapHash
    {
        get;
        init;
    }

    public string? ActualBeatmapHash
    {
        get;
        init;
    }
}

public sealed class ReplayUnsupportedException : ReplayAnalysisException
{
    public ReplayUnsupportedException(string message)
        : base("replay.unsupported", message)
    {
    }
}
