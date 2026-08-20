using System.Text;
using Bantz.Core;
using Xunit;

namespace Bantz.Core.Tests;

public sealed class DictationWorkflowTests
{
    [Fact]
    public async Task ButtonFlowWaitsFiveSecondsBeforeInjection()
    {
        var recorder = new RecorderFake();
        var engine = new EngineFake("hello world");
        var injector = new InjectorFake();
        var delay = new DelayFake();
        var snapshots = new List<DictationSnapshot>();
        using var workflow = new DictationWorkflow(
            recorder, engine, injector, delay, () => true, timeProvider: new RecordingTimeProvider());
        workflow.SnapshotChanged += snapshots.Add;

        await workflow.StartAsync(ActivationKind.Button);
        await workflow.StopAsync(ActivationKind.Button);

        Assert.Equal(5, delay.Delays.Count);
        Assert.All(delay.Delays, value => Assert.Equal(TimeSpan.FromSeconds(1), value));
        Assert.Equal([5, 4, 3, 2, 1], snapshots
            .Where(value => value.State == DictationState.AwaitingTarget)
            .Select(value => value.CountdownSeconds));
        Assert.Equal(("hello world", true), injector.LastInjection);
        Assert.Equal(DictationState.Completed, workflow.Snapshot.State);
    }

    [Fact]
    public async Task HotkeyFlowInjectsWithoutCountdown()
    {
        var delay = new DelayFake();
        var injector = new InjectorFake();
        using var workflow = new DictationWorkflow(
            new RecorderFake(), new EngineFake("quick note"), injector, delay, () => false,
            timeProvider: new RecordingTimeProvider());

        await workflow.StartAsync(ActivationKind.Hotkey);
        await workflow.StopAsync(ActivationKind.Hotkey);

        Assert.Empty(delay.Delays);
        Assert.Equal(("quick note", false), injector.LastInjection);
    }

    [Fact]
    public async Task DisabledAutomaticWritingKeepsTranscriptWithoutInjecting()
    {
        var delay = new DelayFake();
        var injector = new InjectorFake();
        using var workflow = new DictationWorkflow(
            new RecorderFake(),
            new EngineFake("copy this instead"),
            injector,
            delay,
            () => true,
            timeProvider: new RecordingTimeProvider(),
            shouldAutomaticallyWrite: () => false);

        await workflow.StartAsync(ActivationKind.Button);
        await workflow.StopAsync(ActivationKind.Button);

        Assert.Empty(delay.Delays);
        Assert.Null(injector.LastInjection);
        Assert.Equal(DictationState.Completed, workflow.Snapshot.State);
        Assert.Equal("copy this instead", workflow.Snapshot.Transcript);
        Assert.Contains("Copy", workflow.Snapshot.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisablingAutomaticWritingDuringCountdownPreventsInjection()
    {
        var autoWrite = true;
        var delay = new ReleasableDelayFake();
        var injector = new InjectorFake();
        using var workflow = new DictationWorkflow(
            new RecorderFake(),
            new EngineFake("do not type this"),
            injector,
            delay,
            () => true,
            _ => 5,
            new RecordingTimeProvider(),
            () => autoWrite);

        await workflow.StartAsync(ActivationKind.Button);
        await workflow.StopAsync(ActivationKind.Button);
        await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        autoWrite = false;
        delay.ReleaseAll();
        await WaitForSnapshotAsync(
            workflow,
            value => value.Status.Contains("writing is off", StringComparison.OrdinalIgnoreCase));

        Assert.Null(injector.LastInjection);
        Assert.Equal("do not type this", workflow.Snapshot.Transcript);
    }

    [Fact]
    public async Task ShortcutCanUseItsOwnConfiguredDelay()
    {
        var delay = new DelayFake();
        using var workflow = new DictationWorkflow(
            new RecorderFake(),
            new EngineFake("delayed shortcut"),
            new InjectorFake(),
            delay,
            () => false,
            activation => activation == ActivationKind.Hotkey ? 3 : 5,
            new RecordingTimeProvider());

        await workflow.StartAsync(ActivationKind.Hotkey);
        await workflow.StopAsync(ActivationKind.Hotkey);

        Assert.Equal(3, delay.Delays.Count);
    }

    [Fact]
    public async Task ConfiguredDelayIsClampedToTenSeconds()
    {
        var delay = new DelayFake();
        using var workflow = new DictationWorkflow(
            new RecorderFake(), new EngineFake("bounded"), new InjectorFake(), delay, () => false, _ => 99,
            new RecordingTimeProvider());

        await workflow.StartAsync(ActivationKind.Button);
        await workflow.StopAsync(ActivationKind.Button);

        Assert.Equal(10, delay.Delays.Count);
    }

    [Fact]
    public async Task AReleaseFromTheWrongActivationDoesNothing()
    {
        var recorder = new RecorderFake();
        using var workflow = new DictationWorkflow(
            recorder, new EngineFake("ignored"), new InjectorFake(), new DelayFake(), () => false,
            timeProvider: new RecordingTimeProvider());

        await workflow.StartAsync(ActivationKind.Button);
        await workflow.StopAsync(ActivationKind.Hotkey);

        Assert.Equal(DictationState.Recording, workflow.Snapshot.State);
        Assert.Equal(0, recorder.StopCount);
    }

    [Fact]
    public async Task InjectionFailureKeepsTranscriptVisible()
    {
        var injector = new InjectorFake(TextInjectionResult.Failure("Choose another window."));
        using var workflow = new DictationWorkflow(
            new RecorderFake(), new EngineFake("do not lose me"), injector, new DelayFake(), () => false,
            timeProvider: new RecordingTimeProvider());

        await workflow.StartAsync(ActivationKind.Hotkey);
        await workflow.StopAsync(ActivationKind.Hotkey);

        Assert.Equal(DictationState.Error, workflow.Snapshot.State);
        Assert.Equal("do not lose me", workflow.Snapshot.Transcript);
        Assert.Equal("Choose another window.", workflow.Snapshot.Status);
    }

    [Fact]
    public async Task AccidentalHitUnderOnePointFiveSecondsIsDiscarded()
    {
        var clock = new ManualTimeProvider();
        var recorder = new RecorderFake();
        var engine = new EngineFake("must not run");
        var injector = new InjectorFake();
        using var workflow = new DictationWorkflow(
            recorder, engine, injector, new DelayFake(), () => false, timeProvider: clock);

        await workflow.StartAsync(ActivationKind.Hotkey);
        clock.Advance(TimeSpan.FromMilliseconds(1499));
        await workflow.StopAsync(ActivationKind.Hotkey);

        Assert.Equal(1, recorder.StopCount);
        Assert.Equal(0, engine.CallCount);
        Assert.Null(injector.LastInjection);
        Assert.Equal(DictationState.Ready, workflow.Snapshot.State);
        Assert.Contains("1.5 seconds", workflow.Snapshot.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CountdownCanBeCancelledWithoutInjectingText()
    {
        var delay = new BlockingDelayFake();
        var injector = new InjectorFake();
        using var workflow = new DictationWorkflow(
            new RecorderFake(),
            new EngineFake("keep this visible"),
            injector,
            delay,
            () => true,
            _ => 5,
            new RecordingTimeProvider());

        await workflow.StartAsync(ActivationKind.Button);
        await workflow.StopAsync(ActivationKind.Button);
        await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var cancelled = WaitForSnapshotAsync(
            workflow,
            value => value.Status.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
        Assert.True(workflow.CancelPendingInjection());
        await cancelled;

        Assert.Null(injector.LastInjection);
        Assert.Equal(DictationState.Ready, workflow.Snapshot.State);
        Assert.Equal("keep this visible", workflow.Snapshot.Transcript);
        Assert.Contains("cancelled", workflow.Snapshot.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PttPressExtendsCountdownAndPendingTextCanTypeDuringRecording()
    {
        var clock = new ManualTimeProvider();
        var delay = new ReleasableDelayFake();
        var injector = new InjectorFake();
        using var workflow = new DictationWorkflow(
            new RecorderFake(),
            new EngineFake("first transcript"),
            injector,
            delay,
            () => false,
            _ => 5,
            clock);

        await workflow.StartAsync(ActivationKind.Button);
        clock.Advance(TimeSpan.FromSeconds(2));
        await workflow.StopAsync(ActivationKind.Button);
        await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var extended = WaitForSnapshotAsync(
            workflow,
            value => value is
            {
                State: DictationState.Recording,
                CountdownTotalSeconds: 7,
            });
        await workflow.StartAsync(ActivationKind.Button);
        await extended;

        delay.ReleaseAll();
        await injector.Injected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(workflow.IsRecording(ActivationKind.Button));
        Assert.Equal(("first transcript", false), injector.LastInjection);

        clock.Advance(TimeSpan.FromSeconds(2));
        await workflow.StopAsync(ActivationKind.Button);
    }

    [Fact]
    public async Task ShortPttTapDoesNotCancelAnExtendedPendingTranscript()
    {
        var clock = new ManualTimeProvider();
        var delay = new ReleasableDelayFake();
        var injector = new InjectorFake();
        using var workflow = new DictationWorkflow(
            new RecorderFake(),
            new EngineFake("keep pending"),
            injector,
            delay,
            () => false,
            _ => 5,
            clock);

        await workflow.StartAsync(ActivationKind.Hotkey);
        clock.Advance(TimeSpan.FromSeconds(2));
        await workflow.StopAsync(ActivationKind.Hotkey);
        await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await workflow.StartAsync(ActivationKind.Hotkey);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await workflow.StopAsync(ActivationKind.Hotkey);
        delay.ReleaseAll();
        await injector.Injected.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(("keep pending", false), injector.LastInjection);
        Assert.Equal(1, injector.InjectionCount);
    }

    private static async Task<DictationSnapshot> WaitForSnapshotAsync(
        DictationWorkflow workflow,
        Func<DictationSnapshot, bool> predicate)
    {
        if (predicate(workflow.Snapshot))
        {
            return workflow.Snapshot;
        }

        var completion = new TaskCompletionSource<DictationSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Handle(DictationSnapshot snapshot)
        {
            if (predicate(snapshot))
            {
                completion.TrySetResult(snapshot);
            }
        }

        workflow.SnapshotChanged += Handle;
        try
        {
            if (predicate(workflow.Snapshot))
            {
                return workflow.Snapshot;
            }

            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            workflow.SnapshotChanged -= Handle;
        }
    }

    private sealed class RecorderFake : IAudioRecorder
    {
        public int StopCount { get; private set; }
        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<Stream> StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return ValueTask.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("wave")));
        }
    }

    private sealed class EngineFake(string result) : ITranscriptionEngine
    {
        public int CallCount { get; private set; }

        public Task<string> TranscribeAsync(Stream waveAudio, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class InjectorFake(TextInjectionResult? result = null) : ITextInjector
    {
        public (string Text, bool Enter)? LastInjection { get; private set; }
        public int InjectionCount { get; private set; }
        public TaskCompletionSource Injected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TextInjectionResult InjectIntoForeground(string text, bool pressEnter)
        {
            InjectionCount++;
            LastInjection = (text, pressEnter);
            Injected.TrySetResult();
            return result ?? TextInjectionResult.Success();
        }
    }

    private sealed class DelayFake : IAsyncDelay
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingDelayFake : IAsyncDelay
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ReleasableDelayFake : IAsyncDelay
    {
        private readonly object _sync = new();
        private readonly List<TaskCompletionSource> _pending = [];
        private bool _released;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Started.TrySetResult();
                if (_released)
                {
                    return Task.CompletedTask;
                }

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Add(completion);
                cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
                return completion.Task;
            }
        }

        public void ReleaseAll()
        {
            TaskCompletionSource[] pending;
            lock (_sync)
            {
                _released = true;
                pending = [.. _pending];
                _pending.Clear();
            }

            foreach (var completion in pending)
            {
                completion.TrySetResult();
            }
        }
    }

    private sealed class RecordingTimeProvider : TimeProvider
    {
        private int _calls;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _calls++ == 0 ? 0 : TimeSpan.FromSeconds(2).Ticks;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
