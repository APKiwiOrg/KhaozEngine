using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Diagnostic-only pools retained by the production session's three-frame submission batches.</summary>
internal sealed class CoverageDispatchProfiler : IGpuFrameCompletionConsumer, IDisposable
{
    internal static readonly CoverageGpuPhase[] Phases = Enum.GetValues<CoverageGpuPhase>();
    internal static readonly uint MarkerCount = checked((uint)(Phases.Length * 2 + 2));
    readonly Slot[] _slots = new Slot[CoveragePerfManifest.BatchFrameCount];
    readonly CoverageGpuBatch _batch;
    readonly List<CoverageDispatchFrame> _frames = [];
    Slot? _active;
    IGpuCommandList? _commands;
    int _next, _marker;
    bool _disposed;
    internal IReadOnlyList<CoverageDispatchFrame> Frames => _frames;

    internal CoverageDispatchProfiler(IGpuDevice device, CoverageGpuBatch batch)
    {
        _batch = batch;
        try
        {
            for (int i = 0; i < _slots.Length; i++) _slots[i] = new(device.Factory.CreateTimestampPool(MarkerCount));
        }
        catch
        {
            foreach (var slot in _slots) slot?.Pool.Dispose();
            throw;
        }
    }

    internal void Observe(string name, Frame frame)
    {
        if (name == "resolution-start")
        {
            if (_active is not null || _next == _slots.Length) throw new InvalidOperationException("Complete the diagnostic batch first.");
            var slot = _slots[_next++];
            if (slot.Fence is not null) throw new InvalidOperationException("A diagnostic pool still retains its prior frame.");
            if (!frame.Submission.TryRetain(this, out var completion)) throw new InvalidOperationException("Cannot retain the diagnostic completion fence.");
            slot.Fence = completion;
            slot.Ended = false;
            _active = slot;
            _commands = frame.Commands;
            _marker = 1;
            frame.Commands.BeginTimestamps(slot.Pool, MarkerCount);
            frame.Commands.WriteTimestamp(slot.Pool, 0);
        }
        else if (name == "resolution-stop")
        {
            var slot = _active ?? throw new InvalidOperationException("No diagnostic resolution is open.");
            if (_marker != MarkerCount - 1) throw new InvalidOperationException("The diagnostic missed a resolution dispatch.");
            frame.Commands.WriteTimestamp(slot.Pool, (uint)_marker);
            frame.Commands.EndTimestamps(slot.Pool);
            slot.Ended = true;
            _active = null;
            _commands = null;
        }
    }

    internal void Dispatch(IGpuCommandList commands, CoverageGpuPhase phase, bool entering)
    {
        var slot = _active ?? throw new InvalidOperationException("A dispatch is outside the resolution span.");
        int ordinal = (_marker - 1) / 2;
        if (!ReferenceEquals(commands, _commands) || ordinal >= Phases.Length || Phases[ordinal] != phase
            || entering != ((_marker & 1) == 1))
            throw new InvalidOperationException("The diagnostic dispatch order changed.");
        if (entering)
        {
            uint x = CoverageGpuSkinRefineGlsl.IsReducer(phase) ? 1u : (_batch.Elements(phase) + 63) / 64;
            slot.Groups[ordinal] = new(x, (uint)_batch.Count, 1);
        }
        commands.WriteTimestamp(slot.Pool, (uint)_marker++);
    }

    internal void CompleteBatch()
    {
        if (_active is not null) throw new InvalidOperationException("The resolution span did not close.");
        for (int i = 0; i < _next; i++)
        {
            var slot = _slots[i];
            if (!slot.Ended || slot.Fence is null) throw new InvalidOperationException("An unsubmitted diagnostic frame cannot publish timestamps.");
            var status = slot.Pool.TryResolve(slot.Fence, slot.Ticks, out double period);
            if (status != GpuTimestampStatus.Ready || !double.IsFinite(period) || period <= 0 || slot.Ticks[0] != 0)
                throw new InvalidOperationException($"Diagnostic timestamps are unusable after the session drain: {status}.");
            for (int marker = 1; marker < slot.Ticks.Length; marker++)
                if (slot.Ticks[marker] < slot.Ticks[marker - 1]) throw new InvalidOperationException("Diagnostic timestamps moved backwards.");
            var stages = new double[Phases.Length];
            for (int phase = 0; phase < stages.Length; phase++)
                stages[phase] = (slot.Ticks[2 * phase + 2] - slot.Ticks[2 * phase + 1]) * period / 1000;
            double total = slot.Ticks[^1] * period / 1000;
            if (!double.IsFinite(total) || total <= 0) throw new InvalidOperationException("The diagnostic resolution interval is empty.");
            _frames.Add(new(total, stages, (CoverageDispatchGroups[])slot.Groups.Clone(), (ulong[])slot.Ticks.Clone(), period));
            slot.Fence = null;
        }
        _next = 0;
    }

    public bool HasPending(IGpuFence completion)
    {
        foreach (var slot in _slots) if (ReferenceEquals(slot.Fence, completion)) return true;
        return false;
    }

    public void DiscardUnsubmitted(IGpuFence completion) => ReleaseCompleted(completion);

    public void ReleaseCompleted(IGpuFence completion)
    {
        foreach (var slot in _slots)
            if (ReferenceEquals(slot.Fence, completion)) slot.Fence = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        // The caller disposes the session first, which drains and releases every retained frame fence.
        foreach (var slot in _slots)
            if (slot.Fence is not null) throw new InvalidOperationException("Dispose the native session before the diagnostic pools.");
        _disposed = true;
        foreach (var slot in _slots) slot.Pool.Dispose();
    }

    sealed class Slot(IGpuTimestampPool pool)
    {
        internal readonly IGpuTimestampPool Pool = pool;
        internal readonly ulong[] Ticks = new ulong[MarkerCount];
        internal readonly CoverageDispatchGroups[] Groups = new CoverageDispatchGroups[Phases.Length];
        internal IGpuFence? Fence;
        internal bool Ended;
    }
}

internal sealed record CoverageDispatchGroups(uint X, uint Y, uint Z);
internal sealed record CoverageDispatchFrame(double ResolutionMicroseconds, double[] StageMicroseconds,
    CoverageDispatchGroups[] Groups, ulong[] Ticks, double PeriodNanoseconds);
