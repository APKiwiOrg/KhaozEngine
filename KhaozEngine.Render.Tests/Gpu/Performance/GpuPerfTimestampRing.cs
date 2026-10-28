using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Tests.Gpu.Performance;

internal sealed record GpuPerfTimestampResult(ImmutableArray<ulong> Ticks, double? Period, bool Valid, string Reason);

// Adapter-neutral retention. Pass identities and Executed/NotExecuted status belong to the adapter.
internal sealed class GpuPerfTimestampRing : IGpuFrameCompletionConsumer, IGpuFrameSubmissionParticipant, IDisposable
{
    internal const int BatchFrameCount = 3;
    internal const uint MaximumMarkerCount = 512;
    readonly Slot[] _slots = new Slot[BatchFrameCount];
    readonly Action<int, GpuPerfTimestampResult> _publish;
    readonly ImmutableArray<ulong>.Builder[] _samples;
    readonly bool[] _begun, _published;
    readonly uint _markerCount;
    Slot? _recording;
    bool _disposed;

    internal IGpuTimestampPool? ActivePool => _recording?.Pool;
    internal string? Failure { get; private set; }
    internal double? FirstReadyPeriod { get; private set; }

    internal GpuPerfTimestampRing(IGpuDevice device, int sampleCapacity, uint markerCount, Action<int, GpuPerfTimestampResult> publish,
        bool allowSoftware = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCapacity);
        if (markerCount < 2 || markerCount > MaximumMarkerCount)
            throw new ArgumentOutOfRangeException(nameof(markerCount));
        if (!device.Capabilities.SupportsTimestamps || !device.Capabilities.SupportsCompletionFences
            || !allowSoftware && device.Diagnostics.SoftwareAdapter == true)
            throw new NotSupportedException("GPU proof requires hardware timestamps and completion fences.");
        _publish = publish;
        _markerCount = markerCount;
        _begun = new bool[sampleCapacity];
        _published = new bool[sampleCapacity];
        _samples = new ImmutableArray<ulong>.Builder[sampleCapacity];
        for (int i = 0; i < sampleCapacity; i++)
        {
            _samples[i] = ImmutableArray.CreateBuilder<ulong>((int)markerCount);
            _samples[i].Count = (int)markerCount;
        }
        int created = 0;
        try
        {
            for (; created < _slots.Length; created++)
                _slots[created] = new(device.Factory.CreateTimestampPool(markerCount), markerCount);
        }
        catch (Exception error)
        {
            var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
            void Capture(string stage, Exception failure)
            {
                if (failure is GpuDiagnosticFailureException transport) failures.AddRange(transport.Failures);
                else failures.Add(new(stage, failure));
            }
            Capture("renderer.session.construct", error);
            for (int i = 0; i < created; i++)
                try { DisposePool(_slots[i]); }
                catch (Exception cleanup) { Capture("renderer.session.dispose.timestamps." + i, cleanup); }
            if (failures.Count > 1) throw new GpuDiagnosticFailureException(failures.ToImmutable());
            throw;
        }
    }

    internal bool TryBegin(IGpuCommandList commands, GpuFrameSubmission submission, int sampleId, uint markerCount,
        [NotNullWhen(true)] out IGpuTimestampPool? pool)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(submission);
        pool = null;
        if (Failure is not null) return false;
        if (_recording is not null) return Refuse("A timestamp recording is already active.");
        if ((uint)sampleId >= (uint)_samples.Length || markerCount != _markerCount || _begun[sampleId])
            return Refuse("The timestamp layout or sample identity is invalid.");
        if (commands is not IGpuCommandListTimestampEnd)
            return Refuse("The command list has no completed-frame timestamp tail.");
        Slot? free = null;
        foreach (var slot in _slots)
            if (slot.State == State.Free) { free = slot; break; }
        if (free is null) return Refuse("The timestamp ring is full. The block cannot drop a sample.");
        if (!submission.TryRegister(this)) return Refuse("The frame cannot register its timestamp participant.");
        if (!submission.TryRetain(this, out var completion))
            return Refuse("The frame cannot retain its timestamp completion fence.");
        // Retain before touching the pool. A recording error can now only quarantine this slot.
        free.Fence = completion;
        free.SampleId = sampleId;
        free.State = State.Recording;
        _begun[sampleId] = true;
        _recording = free;
        try
        {
            commands.BeginTimestamps(free.Pool, markerCount);
            commands.WriteTimestamp(free.Pool, 0);
        }
        catch
        {
            free.State = State.Quarantined;
            _recording = null;
            Refuse("GPU timestamp recording failed.");
            throw;
        }
        pool = free.Pool;
        return true;
    }

    // Part of the caller's batch completion, after its fence drain and before PollCompleted. Only pools whose query
    // data can trail their fence wait, bounded by the caller's deadline. A failed wait quarantines only its slot.
    internal ImmutableArray<(int SampleId, GpuTimestampAvailability Wait)> AwaitAvailability(Func<long> now, long deadline)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(now);
        var waits = ImmutableArray.CreateBuilder<(int SampleId, GpuTimestampAvailability Wait)>();
        ImmutableArray<GpuDiagnosticFailure>.Builder? failures = null;
        foreach (var slot in _slots)
        {
            if (slot.State != State.Pending || slot.Pool is not IGpuTimestampAvailabilityWait availability) continue;
            try { waits.Add((slot.SampleId, availability.AwaitAvailability(slot.Fence!, now, deadline))); }
            catch (Exception failure)
            {
                slot.State = State.Quarantined;
                Refuse("GPU timestamp resolution or publication failed.");
                failures ??= ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
                if (failure is GpuDiagnosticFailureException transport) failures.AddRange(transport.Failures);
                else failures.Add(new("renderer.batch.complete", failure));
            }
        }
        if (failures is not null) throw new GpuDiagnosticFailureException(failures.ToImmutable());
        waits.Sort((a, b) => a.SampleId.CompareTo(b.SampleId));
        return waits.ToImmutable();
    }

    internal void PollCompleted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // The caller harvests once after its batch drain and AwaitAvailability. This ring adds no completion policy.
        ImmutableArray<GpuDiagnosticFailure>.Builder? failures = null;
        foreach (var slot in _slots)
        {
            if (slot.State != State.Pending) continue;
            // Claim before external calls so a reentrant publication cannot consume this result twice.
            slot.State = State.Quarantined;
            try
            {
                var status = slot.Pool.TryResolve(slot.Fence!, slot.Ticks, out double period);
                if (status == GpuTimestampStatus.Pending)
                {
                    Refuse("Pending GPU timestamps remain after the batch drain.");
                    continue;
                }
                bool valid = status == GpuTimestampStatus.Ready && double.IsFinite(period) && period > 0 && slot.Ticks[0] == 0;
                for (int i = 1; i < slot.Ticks.Length; i++) valid &= slot.Ticks[i] >= slot.Ticks[i - 1];
                string reason = valid ? "" : status == GpuTimestampStatus.Invalid
                    ? "Invalid or disjoint GPU timestamp batch." : "Ready GPU timestamps have an unusable period or marker order.";
                ImmutableArray<ulong> ticks = [];
                if (valid)
                {
                    var storage = _samples[slot.SampleId];
                    for (int i = 0; i < slot.Ticks.Length; i++) storage[i] = slot.Ticks[i];
                    // Each sample transfers its cold storage once, with no later mutation on slot reuse.
                    ticks = storage.MoveToImmutable();
                }
                _publish(slot.SampleId, new(ticks, valid ? period : null, valid, reason));
                if (valid)
                {
                    _published[slot.SampleId] = true;
                    FirstReadyPeriod ??= period;
                    slot.Fence = null;
                    slot.State = State.Free;
                }
                else Refuse(reason);
            }
            catch (Exception failure)
            {
                Refuse("GPU timestamp resolution or publication failed.");
                failures ??= ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
                if (failure is GpuDiagnosticFailureException transport) failures.AddRange(transport.Failures);
                else failures.Add(new("renderer.batch.complete", failure));
            }
        }
        if (failures is not null) throw new GpuDiagnosticFailureException(failures.ToImmutable());
    }

    internal bool CompleteBlock()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Failure is not null) return false;
        foreach (bool published in _published)
            if (!published) return Refuse("A GPU timestamp sample is missing from the block.");
        return true;
    }

    internal void InvalidateBlock(string reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Refuse(reason);
    }

    public bool HasPending(IGpuFence completion)
    {
        // Disposed rings still own quarantined pools until their retained fences are released.
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
        if (_disposed) ReleaseCompleted(completion);
    }

    public void ReleaseCompleted(IGpuFence completion)
    {
        // Teardown only. GpuFrameSubmission drains and calls every consumer before freeing its fences.
        ImmutableArray<GpuDiagnosticFailure>.Builder? failures = null;
        for (int ordinal = 0; ordinal < _slots.Length; ordinal++)
        {
            var slot = _slots[ordinal];
            if (ReferenceEquals(slot.Fence, completion))
            {
                slot.Fence = null;
                slot.State = State.Quarantined;
                if (ReferenceEquals(_recording, slot)) _recording = null;
                if (_disposed)
                    try { DisposePool(slot); }
                    catch (Exception failure)
                    {
                        failures ??= ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
                        if (failure is GpuDiagnosticFailureException transport) failures.AddRange(transport.Failures);
                        else failures.Add(new("renderer.session.dispose.timestamps." + ordinal, failure));
                    }
            }
        }
        if (failures is not null) throw new GpuDiagnosticFailureException(failures.ToImmutable());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recording = null;
        bool retained = false;
        var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        for (int ordinal = 0; ordinal < _slots.Length; ordinal++)
        {
            var slot = _slots[ordinal];
            if (slot.Fence is not null)
            {
                slot.State = State.Quarantined;
                retained = true;
                continue;
            }
            try { DisposePool(slot); }
            catch (Exception failure)
            {
                if (failure is GpuDiagnosticFailureException transport) failures.AddRange(transport.Failures);
                else failures.Add(new("renderer.session.dispose.timestamps." + ordinal, failure));
            }
        }
        if (retained) failures.Add(new("renderer.session.dispose.timestamps.retained",
            new InvalidOperationException("Drain and dispose the frame submission before the timestamp ring.")));
        if (failures.Count != 0) throw new GpuDiagnosticFailureException(failures.ToImmutable());
    }

    static void DisposePool(Slot slot)
    {
        if (slot.PoolDisposed) return;
        slot.PoolDisposed = true;
        slot.Pool.Dispose();
    }

    bool Refuse(string reason) { Failure ??= reason; return false; }
    enum State { Free, Recording, Pending, Quarantined }
    sealed class Slot(IGpuTimestampPool pool, uint markerCount)
    {
        internal readonly IGpuTimestampPool Pool = pool;
        internal readonly ulong[] Ticks = new ulong[markerCount];
        internal IGpuFence? Fence;
        internal int SampleId;
        internal State State;
        internal bool PoolDisposed;
    }
}
