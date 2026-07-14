using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.App;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Runs a sequence of <see cref="IBootStep"/>s while the boot screen renders, folding their weighted progress
    /// into a single overall bar. It drives the async steps on a main-thread pump: <see cref="Start"/> kicks the
    /// sequence off and <see cref="Pump"/> (called once per frame from the render loop) resumes any continuations that
    /// completed off-thread. Because continuations resume on the pump, a step body - and any game asset warm-up in it
    /// - runs on the render thread and may touch GPU resources directly, while its awaited network / disk I/O still
    /// runs off-thread so the bar keeps animating. The observable <see cref="BootView"/> snapshot is guarded by a lock,
    /// so <see cref="Snapshot"/> is safe to read from the render thread while a step reports progress from a pool
    /// thread. Terminal states: <see cref="BootState.Completed"/> (hand off to the game), <see cref="BootState.Failed"/>
    /// (show the error, offer retry / quit), <see cref="BootState.Restarting"/> (the app is relaunching), and
    /// <see cref="BootState.Cancelled"/> (the window closed).
    /// </summary>
    public sealed class BootPipeline
    {
        readonly IReadOnlyList<IBootStep> _steps;
        readonly float[] _starts;
        readonly float[] _sizes;
        readonly object _gate = new();
        readonly PumpContext _pump = new();

        BootView _view;
        CancellationTokenSource? _cts;

        /// <summary>Create a pipeline over <paramref name="steps"/> (run in order). An empty list completes
        /// immediately on the first pump.</summary>
        public BootPipeline(IReadOnlyList<IBootStep> steps)
        {
            _steps = steps ?? throw new ArgumentNullException(nameof(steps));
            var weights = new float[_steps.Count];
            for (int i = 0; i < _steps.Count; i++) weights[i] = _steps[i].Weight;
            (_starts, _sizes) = BootProgressMath.Slices(weights);
            _view = new BootView(BootState.Pending, 0f, false, FirstLabel(), null);
        }

        LocalizedText FirstLabel() => _steps.Count > 0 ? _steps[0].Name : default;

        /// <summary>The current lifecycle state (a shortcut for <see cref="Snapshot"/>.State).</summary>
        public BootState State { get { lock (_gate) return _view.State; } }

        /// <summary>An immutable snapshot of progress for this frame's render. Safe on any thread.</summary>
        public BootView Snapshot() { lock (_gate) return _view; }

        /// <summary>
        /// Begin running the steps. Call once, on the loop thread (the boot scene's OnEnter). Installs the pump as the
        /// current synchronization context for the synchronous kick-off so the first suspension captures it, then
        /// restores the prior context. From here on <see cref="Pump"/> drives the sequence forward. A no-op unless the
        /// state is <see cref="BootState.Pending"/>.
        /// </summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_view.State != BootState.Pending) return;
                _view = _view with { State = BootState.Running };
            }
            _cts = new CancellationTokenSource();
            RunUnderPump(() => _ = RunAllAsync(_cts.Token));
        }

        /// <summary>
        /// Re-run the whole sequence from the start after a <see cref="BootState.Failed"/> outcome (the retry
        /// affordance). The built-in steps are idempotent, so a game's steps should be too. A no-op unless the state is
        /// <see cref="BootState.Failed"/>.
        /// </summary>
        public void Retry()
        {
            lock (_gate)
            {
                if (_view.State != BootState.Failed) return;
                _view = new BootView(BootState.Running, 0f, false, FirstLabel(), null);
            }
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            RunUnderPump(() => _ = RunAllAsync(_cts.Token));
        }

        /// <summary>Request cancellation (e.g. the window is closing). The running step observes it through its token
        /// and the pipeline settles into <see cref="BootState.Cancelled"/>.</summary>
        public void Cancel() => _cts?.Cancel();

        /// <summary>
        /// Resume any step continuations that completed since the last call. Call once per frame from the loop thread
        /// (the boot scene's OnUpdate) BEFORE reading <see cref="Snapshot"/>. Installs the pump context for the drain
        /// so nested awaits re-capture it. Cheap and safe to call in any state.
        /// </summary>
        public void Pump() => RunUnderPump(_pump.Drain);

        // Run an action with the pump installed as the current sync context, restoring the previous one after. The
        // synchronous kick-off (Start) and every drain (Pump) go through here so continuations always target the pump.
        void RunUnderPump(Action action)
        {
            SynchronizationContext? prev = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(_pump);
            try { action(); }
            finally { SynchronizationContext.SetSynchronizationContext(prev); }
        }

        async Task RunAllAsync(CancellationToken ct)
        {
            try
            {
                for (int i = 0; i < _steps.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    IBootStep step = _steps[i];
                    SetStepStart(i, step.Name);

                    var reporter = new SliceReporter(this, i);
                    BootStepResult result = await step.RunAsync(reporter, ct);

                    if (result == BootStepResult.Restarting)
                    {
                        SetTerminal(BootState.Restarting, null);
                        return;
                    }
                    SetStepDone(i);
                }
                SetTerminal(BootState.Completed, null, fraction: 1f);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                SetTerminal(BootState.Cancelled, null);
            }
            catch (BootStepException ex)
            {
                Log.For<BootPipeline>().Warn($"Boot step failed: {ex}");
                SetTerminal(BootState.Failed, ex.LocalizedMessage);
            }
            catch (Exception ex)
            {
                Log.For<BootPipeline>().Error($"Boot step threw: {ex}");
                SetTerminal(BootState.Failed, BootStrings.ErrorGeneric);
            }
        }

        void SetStepStart(int index, LocalizedText label)
        {
            lock (_gate)
                _view = _view with { Fraction = _starts.Length > 0 ? _starts[index] : 0f, Indeterminate = false, StepLabel = label };
        }

        void SetStepDone(int index)
        {
            lock (_gate)
                _view = _view with { Fraction = _starts.Length > 0 ? _starts[index] + _sizes[index] : 1f, Indeterminate = false };
        }

        void ReportStep(int index, float stepFraction)
        {
            lock (_gate)
                _view = _view with { Fraction = BootProgressMath.Overall(index, stepFraction, _starts, _sizes), Indeterminate = false };
        }

        void ReportStepIndeterminate(int index)
        {
            lock (_gate)
                _view = _view with { Fraction = _starts.Length > 0 ? _starts[index] : 0f, Indeterminate = true };
        }

        void SetTerminal(BootState state, LocalizedText? failure, float? fraction = null)
        {
            lock (_gate)
                _view = _view with { State = state, Indeterminate = false, FailureMessage = failure, Fraction = fraction ?? _view.Fraction };
        }

        sealed class SliceReporter : IBootProgress
        {
            readonly BootPipeline _owner;
            readonly int _index;
            public SliceReporter(BootPipeline owner, int index) { _owner = owner; _index = index; }
            public void Report(float fraction) => _owner.ReportStep(_index, fraction);
            public void ReportIndeterminate() => _owner.ReportStepIndeterminate(_index);
        }

        // A minimal single-threaded synchronization context: awaited continuations that complete off-thread Post here
        // and are replayed on the loop thread when Pump drains the queue. This keeps step bodies (and game asset
        // warm-up) on the render thread while their I/O runs off it.
        sealed class PumpContext : SynchronizationContext
        {
            readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

            public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

            public override void Send(SendOrPostCallback d, object? state) => d(state);

            public override SynchronizationContext CreateCopy() => this;

            public void Drain()
            {
                while (_queue.TryDequeue(out var item))
                    item.Callback(item.State);
            }
        }
    }
}
