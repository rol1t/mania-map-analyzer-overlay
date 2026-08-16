using System;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using ManiaMapAnalyzerOverlay.Avalonia.Models;
using Avalonia.Threading;

namespace ManiaMapAnalyzerOverlay.Avalonia.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsStore settingsStore;
    private string status = "tosu is not running";
    private bool isRunning;

    public MainViewModel()
    {
        settingsStore = new SettingsStore();
        Settings = settingsStore.Load();
        Tosu = new TosuService();
        Tosu.StateChanged += OnTosuStateChanged;
    }

    public TosuService Tosu { get; }
    public LauncherSettings Settings { get; }

    public string Status
    {
        get { return status; }
        private set { SetProperty(ref status, value); }
    }

    public bool IsRunning
    {
        get { return isRunning; }
        private set { SetProperty(ref isRunning, value); }
    }

    public string TosuPath => Tosu.ExecutablePath ?? "tosu executable was not found next to the application";

    public async Task StartAsync()
    {
        Status = "Starting tosu…";
        await Tosu.StartAsync();
    }

    public async Task RestartAsync()
    {
        Status = "Restarting tosu…";
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
            Status = e.Message;
            IsRunning = e.IsRunning;
            OnPropertyChanged(nameof(TosuPath));
        });
    }

    public void Dispose()
    {
        Tosu.StateChanged -= OnTosuStateChanged;
        Tosu.Dispose();
    }
}
