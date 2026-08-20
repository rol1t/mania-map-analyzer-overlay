using System;
using System.IO;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

public sealed class AnalyzerAdapterPackage
{
    public AnalyzerAdapterPackage(
        IAnalyzerAdapter adapter,
        string sourceDirectory,
        string scriptPath,
        string hostSelector,
        string? presetAnchorSelector)
    {
        Adapter = adapter;
        SourceDirectory = sourceDirectory;
        ScriptPath = scriptPath;
        HostSelector = hostSelector;
        PresetAnchorSelector = presetAnchorSelector;
    }

    public IAnalyzerAdapter Adapter
    {
        get;
    }
    public AnalyzerDescriptor Descriptor => Adapter.Descriptor;
    public string SourceDirectory
    {
        get;
    }
    public string ScriptPath
    {
        get;
    }
    public string HostSelector
    {
        get;
    }
    public string? PresetAnchorSelector
    {
        get;
    }

    public Uri GetAnalysisUri(Uri serverBaseUri) => new(serverBaseUri, Descriptor.AnalysisPath);

    public Uri? GetSettingsUri(Uri serverBaseUri) => string.IsNullOrWhiteSpace(Descriptor.SettingsPath)
        ? null
        : new Uri(serverBaseUri, Descriptor.SettingsPath);

    public bool MatchesAnalysisUri(Uri? uri) => uri is not null &&
        uri.AbsolutePath.StartsWith(new Uri(new Uri("http://localhost"), Descriptor.AnalysisPath).AbsolutePath,
            StringComparison.OrdinalIgnoreCase);

    public string ReadBridgeScript() => File.ReadAllText(ScriptPath);
}
