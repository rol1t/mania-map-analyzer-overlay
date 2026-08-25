using System.Threading.Channels;

namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>
/// Serializes application events. The state projection is independent from
/// presentation; during the incremental cutover native visibility consumes
/// this state while the legacy WebView publisher remains in place.
/// </summary>
public sealed class OverlayRuntimeCoordinator : IAsyncDisposable
{
    private readonly Channel<PendingEvent> _events = Channel.CreateUnbounded<PendingEvent>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly object _writeGate = new();
    private readonly Task _processingTask;
    private OverlayRuntimeState _current = OverlayRuntimeState.Empty;
    private long _nextSequence;
    private int _stopped;

    public OverlayRuntimeCoordinator()
    {
        _processingTask = ProcessEventsAsync();
    }

    public OverlayRuntimeState Current => Volatile.Read(ref _current);

    /// <summary>
    /// Raised after the reducer has processed an event. Handlers run on the
    /// coordinator consumer and are isolated from the event loop: a diagnostic
    /// observer must never be able to break runtime processing.
    /// </summary>
    public event EventHandler<OverlayRuntimeTransition>? TransitionApplied;

    /// <summary>
    /// Raised after an accepted realtime or analysis transition has been
    /// composed into the immutable presentation contract. Presenters may be
    /// unavailable or hidden; they retain/deliver this state independently.
    /// </summary>
    public event EventHandler<OverlayViewStateChangedEventArgs>? ViewStateChanged;

    public bool TryPost(OverlayRuntimeEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        lock (_writeGate)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                return false;
            }

            _nextSequence = Math.Max(_nextSequence, runtimeEvent.Sequence);
            return _events.Writer.TryWrite(new PendingEvent(runtimeEvent));
        }
    }

    public bool TryPost(Func<long, OverlayRuntimeEvent> createEvent)
    {
        ArgumentNullException.ThrowIfNull(createEvent);
        lock (_writeGate)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                return false;
            }

            OverlayRuntimeEvent runtimeEvent = createEvent(++_nextSequence);
            return _events.Writer.TryWrite(new PendingEvent(runtimeEvent));
        }
    }

    public Task<OverlayRuntimeState> DispatchAsync(
        OverlayRuntimeEvent runtimeEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<OverlayRuntimeState>(cancellationToken);
        }

        var completion = new TaskCompletionSource<OverlayRuntimeState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingEvent(runtimeEvent, completion);
        if (cancellationToken.CanBeCanceled)
        {
            pending.Cancellation = cancellationToken.Register(
                static state =>
                {
                    if (state is CancellationRegistrationState registrationState)
                    {
                        registrationState.Completion.TrySetCanceled(registrationState.Token);
                    }
                },
                new CancellationRegistrationState(completion, cancellationToken));
        }

        lock (_writeGate)
        {
            if (Volatile.Read(ref _stopped) != 0 || !_events.Writer.TryWrite(pending))
            {
                pending.Cancellation.Dispose();
                return Task.FromException<OverlayRuntimeState>(
                    new InvalidOperationException("The overlay runtime coordinator is not accepting events."));
            }

            _nextSequence = Math.Max(_nextSequence, runtimeEvent.Sequence);
        }

        return completion.Task;
    }

    /// <summary>
    /// Allocates the event sequence and enqueues the event under the same
    /// write lock. Callers that need acknowledgement must not read
    /// <see cref="Current"/> and then manufacture a sequence separately: a
    /// realtime transition can be queued between those two operations.
    /// </summary>
    public Task<OverlayRuntimeState> DispatchAsync(
        Func<long, OverlayRuntimeEvent> createEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createEvent);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<OverlayRuntimeState>(cancellationToken);
        }

        var completion = new TaskCompletionSource<OverlayRuntimeState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_writeGate)
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                return Task.FromException<OverlayRuntimeState>(
                    new InvalidOperationException("The overlay runtime coordinator is not accepting events."));
            }

            OverlayRuntimeEvent runtimeEvent = createEvent(++_nextSequence);
            var pending = new PendingEvent(runtimeEvent, completion);
            if (cancellationToken.CanBeCanceled)
            {
                pending.Cancellation = cancellationToken.Register(
                    static state =>
                    {
                        if (state is CancellationRegistrationState registrationState)
                        {
                            registrationState.Completion.TrySetCanceled(registrationState.Token);
                        }
                    },
                    new CancellationRegistrationState(completion, cancellationToken));
            }

            if (!_events.Writer.TryWrite(pending))
            {
                pending.Cancellation.Dispose();
                return Task.FromException<OverlayRuntimeState>(
                    new InvalidOperationException("The overlay runtime coordinator is not accepting events."));
            }
        }

        return completion.Task;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_writeGate)
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                _events.Writer.TryComplete();
            }
        }

        await _processingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task ProcessEventsAsync()
    {
        try
        {
            await foreach (PendingEvent pending in _events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (pending.Completion?.Task.IsCanceled == true)
                    {
                        continue;
                    }

                    OverlayRuntimeState previous = _current;
                    OverlayRuntimeState next = OverlayRuntimeReducer.Apply(previous, pending.Event);
                    Volatile.Write(ref _current, next);
                    bool accepted = !ReferenceEquals(previous, next);
                    PublishTransition(new OverlayRuntimeTransition(
                        pending.Event,
                        previous,
                        next,
                        accepted,
                        accepted ? null : OverlayRuntimeReducer.DescribeRejection(previous, pending.Event)));
                    if (HasViewStateChange(previous, next))
                    {
                        PublishViewState(OverlayViewStateComposer.Compose(next));
                    }
                    pending.Completion?.TrySetResult(next);
                }
                catch (Exception exception)
                {
                    pending.Completion?.TrySetException(exception);
                }
                finally
                {
                    pending.Cancellation.Dispose();
                }
            }
        }
        finally
        {
            while (_events.Reader.TryRead(out PendingEvent? pending))
            {
                pending.Completion?.TrySetException(
                    new InvalidOperationException("The overlay runtime coordinator stopped before processing the event."));
                pending.Cancellation.Dispose();
            }

            Interlocked.Exchange(ref _stopped, 1);
        }
    }

    private void PublishTransition(OverlayRuntimeTransition transition)
    {
        EventHandler<OverlayRuntimeTransition>? handlers = TransitionApplied;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<OverlayRuntimeTransition> handler in handlers.GetInvocationList().Cast<EventHandler<OverlayRuntimeTransition>>())
        {
            try
            {
                handler(this, transition);
            }
            catch
            {
                // Diagnostics are observational only. A logger or comparison
                // callback must not fault the serialized runtime loop.
            }
        }
    }

    private void PublishViewState(OverlayViewState viewState)
    {
        EventHandler<OverlayViewStateChangedEventArgs>? handlers = ViewStateChanged;
        if (handlers is null)
        {
            return;
        }

        var args = new OverlayViewStateChangedEventArgs(viewState);
        foreach (EventHandler<OverlayViewStateChangedEventArgs> handler in handlers.GetInvocationList().Cast<EventHandler<OverlayViewStateChangedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // A presenter is an external effect. Its failure must not
                // stop the serialized runtime reducer.
            }
        }
    }

    private static bool HasViewStateChange(OverlayRuntimeState previous, OverlayRuntimeState next) =>
        !ReferenceEquals(previous.LatestRealtime, next.LatestRealtime)
        || !ReferenceEquals(previous.LatestAnalysis, next.LatestAnalysis)
        || previous.OverlayMode != next.OverlayMode
        || !string.Equals(previous.VisibilityPolicy, next.VisibilityPolicy, StringComparison.Ordinal)
        || previous.OsuWindowMinimized != next.OsuWindowMinimized
        || previous.PresentationReady != next.PresentationReady
        || previous.PresentationVisible != next.PresentationVisible
        || previous.PresentationSurfaceGeneration != next.PresentationSurfaceGeneration;

    private sealed class PendingEvent
    {
        public PendingEvent(
            OverlayRuntimeEvent runtimeEvent,
            TaskCompletionSource<OverlayRuntimeState>? completion = null)
        {
            Event = runtimeEvent;
            Completion = completion;
        }

        public OverlayRuntimeEvent Event
        {
            get;
        }

        public TaskCompletionSource<OverlayRuntimeState>? Completion
        {
            get;
        }

        public CancellationTokenRegistration Cancellation
        {
            get; set;
        }
    }

    private sealed record CancellationRegistrationState(
        TaskCompletionSource<OverlayRuntimeState> Completion,
        CancellationToken Token);
}
