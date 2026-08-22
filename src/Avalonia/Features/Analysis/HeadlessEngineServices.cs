using System;
using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

/// <summary>
/// Cohesive dependencies required to create and run a headless analyzer engine.
/// Keeps the controller constructor free of loosely grouped parameters.
/// </summary>
public sealed record HeadlessEngineServices(
    AnalyzerEngineCatalog Catalog,
    AnalyzerEnginePackageDeployer Deployer,
    Func<IAnalyzerScriptHost> ScriptHostFactory);
