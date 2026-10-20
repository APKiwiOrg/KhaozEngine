using System;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

internal sealed class CoveragePerfTimestampRing : IGpuFrameCompletionConsumer, IGpuFrameSubmissionParticipant, IDisposable
{
    readonly Slot[] _slots = new Slot[CoveragePerfManifest.BatchFrameCount];
    readonly Action<int, CoveragePerfGpuResult> _publish;
    readonly ImmutableArray<ulong>.Builder[] _samples;
    readonly bool[] _begun;
    readonly CoveragePerfTimerLayout _layout;
    Slot? _recording;
    bool _disposed;
    internal IGpuTimestampPool? ActivePool => _recording?.Pool;
    internal string? Failure { get; private set; }
    internal double? FirstReadyPeriod { get; private set; }

    internal CoveragePerfTimestampRing(IGpuDevice device, int sampleCapacity, CoveragePerfTimerLayout layout,
        Action<int, CoveragePerfGpuResult> publish)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCapacity);
        if (layout is not (CoveragePerfTimerLayout.Total or CoveragePerfTimerLayout.Phases))
            throw new ArgumentOutOfRangeException(nameof(layout));
        if (!device.Capabilities.SupportsTimestamps || !device.Capabilities.SupportsCompletionFences
            || device.Diagnostics.SoftwareAdapter == true)
            throw new NotSupportedException("GPU proof requires hardware timestamps and completion fences.");
        _publish = publish;
        _layout = layout;
        _begun = new bool[sampleCapacity];
        _samples = new ImmutableArray<ulong>.Builder[sampleCapacity];
        int count = layout == CoveragePerfTimerLayout.Total ? 2 : 5;
        for (int i = 0; i < sampleCapacity; i++)
        {
            _samples[i] = ImmutableArray.CreateBuilder<ulong>(count);
            _samples[i].Count = count;
        }
        int created = 0;
        try
        {
            for (; created < _slots.Length; created++) _slots[created] = new(device.Factory.CreateTimestampPool(5));
        }
        catch
        {
            for (int i = 0; i < created; i++) _slots[i].Pool.Dispose();
            throw;
        }
    }

    internal bool TryBegin(IGpuCommandList commands, GpuFrameSubmission submission, int sampleIndex, CoveragePerfTimerLayout layout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Failure is not null) return false;
        if (_recording is not null) return Refuse("A timestamp recording is already active.");
        if ((uint)sampleIndex >= (uint)_samples.Length || layout != _layout || _begun[sampleIndex])
            return Refuse("The timestamp layout or sample identity is invalid.");
        if (commands is not IGpuCommandListTimestampEnd) return Refuse("The command list has no completed-frame timestamp tail.");
        Slot? free = null;
        foreach (var slot in _slots)
            if (slot.State == State.Free) { free = slot; break; }
        if (free is null) return Refuse("The timestamp ring is full. The block cannot drop a sample.");
        if (!submission.TryRegister(this)) return Refuse("The frame cannot register its timestamp participant.");
        if (!submission.TryRetain(this, out var completion)) return Refuse("The frame cannot retain its timestamp completion fence.");
        // Retain before touching the pool. A recording error can now only quarantine this slot.
        free.Fence = completion;
        free.SampleIndex = sampleIndex;
        free.Layout = layout;
        free.Count = layout == CoveragePerfTimerLayout.Total ? 2 : 5;
        free.State = State.Recording;
        _begun[sampleIndex] = true;
        _recording = free;
        commands.BeginTimestamps(free.Pool, (uint)free.Count);
        commands.WriteTimestamp(free.Pool, 0);
        return true;
    }

    internal void PollCompleted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // The session calls once after its existing batch drain. There is no completion policy here.
        ExceptionDispatchInfo? error = null;
        foreach (var slot in _slots)
        {
            if (slot.State != State.Pending) continue;
            try
            {
                var status = slot.Pool.TryResolve(slot.Fence!, slot.Ticks, out double period);
                if (status == GpuTimestampStatus.Pending)
                {
                    slot.State = State.Quarantined;
                    Refuse("Pending GPU timestamps remain after the batch drain.");
                    continue;
                }
                bool valid = status == GpuTimestampStatus.Ready && double.IsFinite(period) && period > 0 && slot.Ticks[0] == 0;
                for (int i = 1; i < slot.Count; i++) valid &= slot.Ticks[i] >= slot.Ticks[i - 1];
                string reason = valid ? "" : status == GpuTimestampStatus.Invalid
                    ? "Invalid or disjoint GPU timestamp batch." : "Ready GPU timestamps have an unusable period or marker order.";
                // Immutable publication and validation stay outside the aggregate CPU interval.
                ImmutableArray<ulong> ticks = [];
                if (valid)
                {
                    var storage = _samples[slot.SampleIndex];
                    for (int i = 0; i < slot.Count; i++) storage[i] = slot.Ticks[i];
                    // Each frame owns its cold buffer. Transfer it once without allocating or later mutating it.
                    ticks = storage.MoveToImmutable();
                }
                var result = new CoveragePerfGpuResult(slot.Layout, ticks, period, valid, false, reason);
                _publish(slot.SampleIndex, result);
                if (valid)
                {
                    FirstReadyPeriod ??= period;
                    slot.Fence = null;
                    slot.State = State.Free;
                }
                else
                {
                    slot.State = State.Quarantined;
                    Refuse(reason);
                }
            }
            catch (Exception failure)
            {
                slot.State = State.Quarantined;
                Refuse("GPU timestamp resolution or publication failed.");
                error ??= ExceptionDispatchInfo.Capture(failure);
            }
        }
        error?.Throw();
    }

    public bool HasPending(IGpuFence completion)
    {
        if (_disposed) return false;
        foreach (var slot in _slots) if (ReferenceEquals(slot.Fence, completion)) return true;
        return false;
    }

    public void Submitted()
    {
        if (_recording is not { } slot) return;
        slot.State = State.Pending;
        _recording = null;
    }

    public void Abandoned()
    {
        if (_recording is not { } slot) return;
        slot.State = State.Quarantined;
        _recording = null;
        Refuse("The GPU timestamp recording was abandoned or submission failed.");
    }

    public void DiscardUnsubmitted(IGpuFence completion)
    {
        foreach (var slot in _slots)
            if (ReferenceEquals(slot.Fence, completion))
            {
                slot.State = State.Quarantined;
                Refuse("The GPU timestamp recording was discarded before submission.");
            }
    }

    public void ReleaseCompleted(IGpuFence completion)
    {
        // Teardown only. GpuFrameSubmission owns the drain and calls every consumer before freeing fences.
        foreach (var slot in _slots)
            if (ReferenceEquals(slot.Fence, completion))
            {
                slot.Fence = null;
                slot.State = State.Quarantined;
                if (ReferenceEquals(_recording, slot)) _recording = null;
            }
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var slot in _slots)
            if (slot.Fence is not null) throw new InvalidOperationException("Drain and dispose the frame submission before the timestamp ring.");
        _disposed = true;
        foreach (var slot in _slots) slot.Pool.Dispose();
    }

    bool Refuse(string reason) { Failure ??= reason; return false; }
    enum State { Free, Recording, Pending, Quarantined }
    sealed class Slot(IGpuTimestampPool pool)
    {
        internal readonly IGpuTimestampPool Pool = pool;
        internal readonly ulong[] Ticks = new ulong[5];
        internal IGpuFence? Fence;
        internal int SampleIndex, Count;
        internal CoveragePerfTimerLayout Layout;
        internal State State;
    }
}
