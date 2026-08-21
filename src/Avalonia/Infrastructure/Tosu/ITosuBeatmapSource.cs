using System.Threading;
using System.Threading.Tasks;

namespace ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;

/// <summary>
/// Reads the current beatmap from tosu without depending on a widget's HTML or
/// DOM structure.
/// </summary>
public interface ITosuBeatmapSource
{
    Task<TosuBeatmapSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default);
}
