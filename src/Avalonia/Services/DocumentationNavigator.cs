using System;
using System.Diagnostics;
using Avalonia.Controls;
using ManiaMapAnalyzerOverlay.Avalonia.Views;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

public static class DocumentationNavigator
{
    public static bool IsDocumentationLink(string url)
    {
        return DocumentationService.IsDocumentationLink(url);
    }

    public static void Open(string url, Window? owner = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (DocumentationService.IsDocumentationLink(url))
        {
            var id = DocumentationService.ExtractDocId(url);
            if (!string.IsNullOrWhiteSpace(id))
            {
                OpenDocument(id, owner);
                return;
            }
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                AppLogger.Warning("Opening external link", $"Could not open '{url}': {exception.Message}", exception);
            }

            return;
        }

        // Fallback: try to treat as doc id directly
        if (!url.Contains("://", StringComparison.Ordinal))
        {
            OpenDocument(url, owner);
        }
    }

    public static void OpenDocument(string docId, Window? owner = null)
    {
        try
        {
            var dialog = new DocumentationDialog(docId);
            if (owner is not null)
            {
                _ = dialog.ShowDialog(owner);
            }
            else
            {
                var active = GetActiveWindow();
                if (active is not null)
                {
                    _ = dialog.ShowDialog(active);
                }
                else
                {
                    dialog.Show();
                }
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("Opening documentation", exception);
        }
    }

    private static Window? GetActiveWindow()
    {
        try
        {
            if (global::Avalonia.Application.Current?.ApplicationLifetime is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                return desktop.MainWindow;
            }
        }
        catch
        {
        }

        return null;
    }
}
