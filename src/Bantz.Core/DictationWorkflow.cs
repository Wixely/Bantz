namespace Bantz.Core;

public enum DictationState
{
    Ready,
    Recording,
    Transcribing,
    AwaitingTarget,
    Injecting,
    Completed,
    Error,
}

public enum ActivationKind
{
    Button,
    Hotkey,
}

public sealed record DictationSnapshot(
    DictationState State,
    ActivationKind? Activation,
    string Status,
    string Transcript = "",
    int CountdownSeconds = 0,
    int CountdownTotalSeconds = 0)
{
    public static DictationSnapshot Initial { get; } =
        new(DictationState.Ready, null, "Hold to talk");
}

public sealed class DictationWorkflow(
    IAudioRecorder recorder,
    ITranscriptionEngine transcription,
    ITextInjector injector,
    IAsyncDelay delay,
    Func<bool> shouldPressEnter,
    Func<ActivationKind, int>? delaySeconds = null,
    TimeProvider? timeProvider = null) : IDisposable
{
    public static readonly TimeSpan MinimumRecordingDuration = TimeSpan.FromSeconds(1.5);

    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _activitySync = new();
    private readonly object _injectionSync = new();
    private readonly Queue<InjectionRequest> _injectionQueue = [];
    private volatile DictationSnapshot _snapshot = DictationSnapshot.Initial;
    private PendingInjection? _activeInjection;
    private bool _injectionPumpRunning;
    private ActivationKind? _recordingActivation;
    private ActivationKind? _transcribingActivation;
    private long _recordingStarted;
    private bool _disposed;

    public event Action<DictationSnapshot>? SnapshotChanged;

    public DictationSnapshot Snapshot => _snapshot;

    public bool IsRecording(ActivationKind activation)
    {
        lock (_activitySync)
        {
            return _recordingActivation == activation;
        }
    }

    public bool CancelPendingInjection()
    {
        PendingInjection? pending;
        lock (_injectionSync)
        {
            pending = _activeInjection;
        }

        return pending?.Cancel() == true;
    }

    public bool ExtendPendingInjection(TimeSpan extension)
    {
        if (extension <= TimeSpan.Zero)
        {
            return false;
        }

        PendingInjection? pending;
        lock (_injectionSync)
        {
            pending = _activeInjection;
        }

        return pending?.Extend(extension) == true;
    }

    public async Task StartAsync(ActivationKind activation, CancellationToken cancellationToken = default)
    {
        // A fresh PTT press must not race a transcript that is about to be typed.
        // Keep that transcript, but give the user another full accidental-hit buffer.
        ExtendPendingInjection(MinimumRecordingDuration);

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_activitySync)
            {
                if (_recordingActivation is not null || _transcribingActivation is not null)
                {
                    return;
                }
            }

            await recorder.StartAsync(cancellationToken).ConfigureAwait(false);
            _recordingStarted = _timeProvider.GetTimestamp();
            lock (_activitySync)
            {
                _recordingActivation = activation;
            }

            PublishWithPending(new(
                DictationState.Recording,
                activation,
                "Listening… release to transcribe",
                _snapshot.Transcript));
        }
        catch (Exception exception)
        {
            PublishError($"Could not start recording: {exception.Message}", activation);
        }
        finally
        {
            _operation.Release();
        }
    }

    public async Task StopAsync(ActivationKind activation, CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRecording(activation))
            {
                return;
            }

            await using var audio = await recorder.StopAsync(cancellationToken).ConfigureAwait(false);
            var recordingDuration = _timeProvider.GetElapsedTime(_recordingStarted);
            lock (_activitySync)
            {
                _recordingActivation = null;
            }

            if (recordingDuration < MinimumRecordingDuration)
            {
                PublishWithPending(new(
                    DictationState.Ready,
                    null,
                    "Too short — hold for at least 1.5 seconds",
                    _snapshot.Transcript));
                return;
            }

            lock (_activitySync)
            {
                _transcribingActivation = activation;
            }

            PublishWithPending(new(
                DictationState.Transcribing,
                activation,
                "Transcribing locally…",
                _snapshot.Transcript));
            var text = (await transcription.TranscribeAsync(audio, cancellationToken).ConfigureAwait(false)).Trim();
            lock (_activitySync)
            {
                _transcribingActivation = null;
            }

            if (text.Length == 0)
            {
                PublishError("No speech was recognised. Hold and try again.", activation);
                return;
            }

            var secondsToWait = Math.Clamp(delaySeconds?.Invoke(activation) ??
                (activation == ActivationKind.Button ? 5 : 0), 0, 10);
            EnqueueInjection(new(text, activation, TimeSpan.FromSeconds(secondsToWait), cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClearActivity(activation);
            PublishError("Dictation was cancelled.", activation);
        }
        catch (Exception exception)
        {
            ClearActivity(activation);
            PublishError($"Dictation failed: {exception.Message}", activation, _snapshot.Transcript);
        }
        finally
        {
            _operation.Release();
        }
    }

    private void EnqueueInjection(InjectionRequest request)
    {
        var startPump = false;
        lock (_injectionSync)
        {
            if (_disposed)
            {
                return;
            }

            _injectionQueue.Enqueue(request);
            if (!_injectionPumpRunning)
            {
                _injectionPumpRunning = true;
                startPump = true;
            }
        }

        if (startPump)
        {
            _ = ProcessInjectionQueueAsync();
        }
    }

    private async Task ProcessInjectionQueueAsync()
    {
        while (true)
        {
            InjectionRequest request;
            PendingInjection pending;
            lock (_injectionSync)
            {
                if (_disposed || _injectionQueue.Count == 0)
                {
                    _injectionPumpRunning = false;
                    return;
                }

                request = _injectionQueue.Dequeue();
                pending = new PendingInjection(request.Delay, request.CancellationToken);
                _activeInjection = pending;
            }

            try
            {
                var completed = await RunCountdownAsync(request, pending).ConfigureAwait(false);
                if (completed)
                {
                    Inject(request);
                }
                else
                {
                    PublishPreservingActivity(new(
                        DictationState.Ready,
                        null,
                        "Countdown cancelled — transcript was not typed",
                        request.Text));
                }
            }
            catch (OperationCanceledException) when (pending.IsCancellationRequested)
            {
                PublishPreservingActivity(new(
                    DictationState.Ready,
                    null,
                    "Countdown cancelled — transcript was not typed",
                    request.Text));
            }
            catch (Exception exception)
            {
                PublishBackgroundError($"Text could not be typed: {exception.Message}", request);
            }
            finally
            {
                lock (_injectionSync)
                {
                    if (ReferenceEquals(_activeInjection, pending))
                    {
                        _activeInjection = null;
                    }
                }

                pending.Dispose();
            }
        }
    }

    private async Task<bool> RunCountdownAsync(InjectionRequest request, PendingInjection pending)
    {
        while (pending.TryBeginDelay(out var remaining, out var wait, out var wakeToken))
        {
            PublishCountdown(request, remaining, pending.Total);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                pending.CancellationToken,
                wakeToken);
            try
            {
                await delay.DelayAsync(wait, linked.Token).ConfigureAwait(false);
                pending.CompleteDelay(wait);
            }
            catch (OperationCanceledException) when (
                !pending.IsCancellationRequested && wakeToken.IsCancellationRequested)
            {
                // PTT extended the deadline. Re-read the new remaining duration.
            }
        }

        return !pending.IsCancellationRequested;
    }

    private void Inject(InjectionRequest request)
    {
        var injecting = new DictationSnapshot(
            DictationState.Injecting,
            request.Activation,
            "Typing…",
            request.Text);
        PublishPreservingActivity(injecting);

        var result = injector.InjectIntoForeground(request.Text, shouldPressEnter());
        if (!result.Succeeded)
        {
            PublishBackgroundError(
                result.Error ?? "Text could not be typed into the foreground window.",
                request);
            return;
        }

        PublishPreservingActivity(new(
            DictationState.Completed,
            request.Activation,
            "Done — hold to talk again",
            request.Text));
    }

    private void PublishCountdown(InjectionRequest request, TimeSpan remaining, TimeSpan total)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        var totalSeconds = Math.Max(seconds, (int)Math.Ceiling(total.TotalSeconds));
        PublishPreservingActivity(new(
            DictationState.AwaitingTarget,
            request.Activation,
            $"Click the destination — typing in {seconds}",
            request.Text,
            seconds,
            totalSeconds));
    }

    private void PublishWithPending(DictationSnapshot snapshot)
    {
        PendingView? view;
        lock (_injectionSync)
        {
            view = _activeInjection?.View;
        }

        if (view is not null && view.Value.Remaining > TimeSpan.Zero)
        {
            snapshot = snapshot with
            {
                CountdownSeconds = Math.Max(1, (int)Math.Ceiling(view.Value.Remaining.TotalSeconds)),
                CountdownTotalSeconds = Math.Max(1, (int)Math.Ceiling(view.Value.Total.TotalSeconds)),
            };
        }

        Publish(snapshot);
    }

    private void PublishPreservingActivity(DictationSnapshot fallback)
    {
        lock (_activitySync)
        {
            if (_recordingActivation is { } recording)
            {
                Publish(fallback with
                {
                    State = DictationState.Recording,
                    Activation = recording,
                    Status = "Listening… release to transcribe",
                });
                return;
            }

            if (_transcribingActivation is { } transcribing)
            {
                Publish(fallback with
                {
                    State = DictationState.Transcribing,
                    Activation = transcribing,
                    Status = "Transcribing locally…",
                });
                return;
            }
        }

        Publish(fallback);
    }

    private void PublishBackgroundError(string message, InjectionRequest request) =>
        PublishPreservingActivity(new(DictationState.Error, request.Activation, message, request.Text));

    private void PublishError(string message, ActivationKind? activation, string transcript = "") =>
        PublishPreservingActivity(new(DictationState.Error, activation, message, transcript));

    private void Publish(DictationSnapshot snapshot)
    {
        _snapshot = snapshot;
        SnapshotChanged?.Invoke(snapshot);
    }

    private void ClearActivity(ActivationKind activation)
    {
        lock (_activitySync)
        {
            if (_recordingActivation == activation)
            {
                _recordingActivation = null;
            }

            if (_transcribingActivation == activation)
            {
                _transcribingActivation = null;
            }
        }
    }

    public void Dispose()
    {
        PendingInjection? pending;
        lock (_injectionSync)
        {
            _disposed = true;
            _injectionQueue.Clear();
            pending = _activeInjection;
        }

        pending?.Cancel();
        _operation.Dispose();
    }

    private sealed record InjectionRequest(
        string Text,
        ActivationKind Activation,
        TimeSpan Delay,
        CancellationToken CancellationToken);

    private readonly record struct PendingView(TimeSpan Remaining, TimeSpan Total);

    private sealed class PendingInjection : IDisposable
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cancellation;
        private CancellationTokenSource _wake = new();
        private TimeSpan _remaining;
        private TimeSpan _total;
        private long _waitStarted;
        private TimeSpan _waitDuration;
        private bool _waiting;
        private bool _finished;
        private volatile bool _cancelled;

        public PendingInjection(TimeSpan duration, CancellationToken cancellationToken)
        {
            _remaining = duration;
            _total = duration;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public CancellationToken CancellationToken => _cancellation.Token;
        public bool IsCancellationRequested => _cancelled || _cancellation.IsCancellationRequested;

        public TimeSpan Total
        {
            get
            {
                lock (_sync)
                {
                    return _total;
                }
            }
        }

        public PendingView View
        {
            get
            {
                lock (_sync)
                {
                    return new(_remaining, _total);
                }
            }
        }

        public bool Extend(TimeSpan extension)
        {
            CancellationTokenSource? wake;
            lock (_sync)
            {
                if (_finished || _cancelled)
                {
                    return false;
                }

                AccountForElapsedWait();
                _remaining += extension;
                _total += extension;
                wake = _wake;
                _wake = new();
            }

            wake.Cancel();
            wake.Dispose();
            return true;
        }

        public bool Cancel()
        {
            CancellationTokenSource wake;
            lock (_sync)
            {
                if (_finished || _cancelled)
                {
                    return false;
                }

                _cancelled = true;
                wake = _wake;
            }

            // Cancellation callbacks can run synchronously. Never invoke them while
            // holding the state lock that the countdown continuation also needs.
            _cancellation.Cancel();
            wake.Cancel();
            return true;
        }

        public bool TryBeginDelay(
            out TimeSpan remaining,
            out TimeSpan wait,
            out CancellationToken wakeToken)
        {
            lock (_sync)
            {
                if (_remaining <= TimeSpan.Zero || _cancelled)
                {
                    _finished = true;
                    remaining = TimeSpan.Zero;
                    wait = TimeSpan.Zero;
                    wakeToken = default;
                    return false;
                }

                remaining = _remaining;
                wait = TimeSpan.FromSeconds(Math.Min(1, _remaining.TotalSeconds));
                _waitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                _waitDuration = wait;
                _waiting = true;
                wakeToken = _wake.Token;
                return true;
            }
        }

        public void CompleteDelay(TimeSpan duration)
        {
            lock (_sync)
            {
                if (!_waiting)
                {
                    return;
                }

                _waiting = false;
                _remaining = _remaining > duration ? _remaining - duration : TimeSpan.Zero;
            }
        }

        private void AccountForElapsedWait()
        {
            if (!_waiting)
            {
                return;
            }

            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_waitStarted);
            var accounted = elapsed < _waitDuration ? elapsed : _waitDuration;
            _remaining = _remaining > accounted ? _remaining - accounted : TimeSpan.Zero;
            _waiting = false;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _finished = true;
            }

            _cancellation.Dispose();
            _wake.Dispose();
        }
    }
}
