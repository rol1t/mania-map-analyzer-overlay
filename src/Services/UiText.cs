namespace ManiaMapAnalyzerOverlay;

internal static class UiText
{
    public static bool IsEnglish { get; set; } = true;

    public static string Get(string russian, string english) => IsEnglish ? english : russian;
}
