using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using ManiaMapAnalyzerOverlay.Avalonia.Models;
using ManiaMapAnalyzerOverlay.Avalonia.Services;

namespace ManiaMapAnalyzerOverlay.Avalonia.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsStore settingsStore;
    private string status = UiText.Get("status.tosu_not_running");
    private bool isRunning;

    public MainViewModel()
    {
        settingsStore = new SettingsStore();
        Settings = settingsStore.Load();
        Tosu = new TosuService();
        Tosu.StateChanged += OnTosuStateChanged;
    }

    public TosuService Tosu
    {
        get;
    }
    public LauncherSettings Settings
    {
        get;
    }

    public string Status
    {
        get
        {
            return status;
        }
        private set
        {
            SetProperty(ref status, value);
        }
    }

    public bool IsRunning
    {
        get
        {
            return isRunning;
        }
        private set
        {
            SetProperty(ref isRunning, value);
        }
    }

    public string TosuPath => Tosu.ExecutablePath ?? string.Empty;

    public async Task StartAsync()
    {
        Status = UiText.Get("status.tosu_starting");
        await Tosu.StartAsync();
    }

    public async Task RestartAsync()
    {
        Status = UiText.Get("status.tosu_restarting");
        await Tosu.RestartAsync();
    }

    public void Stop() => Tosu.Stop();
    public void SaveSettings() => settingsStore.Save(Settings);
    public void SetStatus(string message, bool running = false)
    {
        Status = message;
        IsRunning = running;
    }

    private void OnTosuStateChanged(object? sender, TosuStateChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Status = LocalizeStateMessage(e.Message);
            IsRunning = e.IsRunning;
            OnPropertyChanged(nameof(TosuPath));
        });
    }

    private static string LocalizeStateMessage(string messageKey)
    {
        const string failurePrefix = "status.tosu_start_failed|";
        return messageKey.StartsWith(failurePrefix, StringComparison.Ordinal)
            ? UiText.Format("status.tosu_start_failed", messageKey[failurePrefix.Length..])
            : UiText.Get(messageKey);
    }

    public void Dispose()
    {
        Tosu.StateChanged -= OnTosuStateChanged;
        Tosu.Dispose();
    }
}
