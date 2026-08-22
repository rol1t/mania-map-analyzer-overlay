using System;
using System.Threading;
using System.Threading.Tasks;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

public sealed class HeadlessPollingLifecycle : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> _pollLoop;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private Task? _task;

    public HeadlessPollingLifecycle(Func<CancellationToken, Task> pollLoop)
    {
        _pollLoop = pollLoop ?? throw new ArgumentNullException(nameof(pollLoop));
    }

    public bool IsRunning => _task is { IsCompleted: false };

    public async Task StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _task = RunAsync(_cancellation.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var task = _task;
            var cancellation = _cancellation;
            _task = null;
            _cancellation = null;
            cancellation?.Cancel();
            try
            {
                if (task is not null)
                {
                    await task.ConfigureAwait(false);
                }
            }
            finally
            {
                cancellation?.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _pollLoop(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
