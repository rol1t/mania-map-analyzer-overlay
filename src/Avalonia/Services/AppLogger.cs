using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

/// <summary>
/// Small process-wide logger used by the desktop shell and its infrastructure
/// services. Logging must never become a second source of failures, therefore
/// file-write errors are sent to the debugger and never escape the logger.
/// </summary>
public static class AppLogger
{
    private static readonly object Sync = new();

    public static event EventHandler<AppLogEntry>? ErrorRaised;

    public static string LogPath => Path.Combine(AppPaths.DataDirectory, "application.log");

    public static void Info(string operation, string message) => Write("INFO", operation, message, null, false);

    public static void Warning(string operation, string message, Exception? exception = null) =>
        Write("WARN", operation, message, exception, false);

    public static void Error(string operation, Exception exception, bool userVisible = true) =>
        Write("ERROR", operation, exception.Message, exception, userVisible);

    public static void Error(string operation, string message, bool userVisible = true) =>
        Write("ERROR", operation, message, null, userVisible);

    private static void Write(
        string level,
        string operation,
        string message,
        Exception? exception,
        bool userVisible)
    {
        var entry = new AppLogEntry(DateTimeOffset.Now, level, operation, message, exception, userVisible);
        var details = exception is null ? message : exception.ToString();
        var line = $"[{entry.Timestamp:O}] [{level}] [{operation}] {details}{Environment.NewLine}";

        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                File.AppendAllText(LogPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception loggingException)
        {
            Debug.WriteLine($"Could not write application log: {loggingException}");
            Debug.WriteLine(line);
        }

        if (level == "ERROR" || (level == "WARN" && exception is not null))
        {
            try
            {
                ErrorRaised?.Invoke(null, entry);
            }
            catch (Exception notificationException) { Debug.WriteLine($"Error notification failed: {notificationException}"); }
        }
    }
}

public sealed class AppLogEntry : EventArgs
{
    public AppLogEntry(
        DateTimeOffset timestamp,
        string level,
        string operation,
        string message,
        Exception? exception,
        bool userVisible)
    {
        Timestamp = timestamp;
        Level = level;
        Operation = operation;
        Message = message;
        Exception = exception;
        UserVisible = userVisible;
    }

    public DateTimeOffset Timestamp
    {
        get;
    }
    public string Level
    {
        get;
    }
    public string Operation
    {
        get;
    }
    public string Message
    {
        get;
    }
    public Exception? Exception
    {
        get;
    }
    public bool UserVisible
    {
        get;
    }
}
