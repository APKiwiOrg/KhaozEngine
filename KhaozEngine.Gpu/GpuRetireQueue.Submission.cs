using System;

namespace KhaozEngine.Gpu;

public sealed partial class GpuRetireQueue : IGpuFrameCompletionConsumer
{
    int _sharedDeferredBoundaries;

    /// <summary>Advance before recording using this host frame's completion fence instead of an empty submission.
    /// The host must submit before the next boundary and outlive this queue, or complete its teardown first.
    /// Missing completion capacity defers sealing and increments <see cref="SharedSealDeferrals"/>.
    /// Sealed batches plus consecutive deferred boundaries share <see cref="MaxSealedBatches"/>' drain bound.
    /// A frame-count-only policy never drains here and requires the host to make completion capacity available.
    /// Devices without completion fences retain the ordinary frame-count and drain fallback.</summary>
    public void BeginFrame(GpuFrameSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!ReferenceEquals(_device, submission.Device))
            throw new GpuResourceOwnershipArgumentException(_device is not null && _device.Backend != submission.Device.Backend
                ? GpuResourceOwnershipRefusal.ForeignBackend : GpuResourceOwnershipRefusal.DifferentDevice,
                "The retirement queue and frame host must use the same device.", nameof(submission))
            {
                ExpectedBackend = _device?.Backend,
                ActualType = submission.Device.GetType(),
                ExpectedType = _device?.GetType(),
                ResourceKind = "frame submission",
                Context = nameof(BeginFrame),
            };
        if (!_device!.Capabilities.SupportsCompletionFences) { BeginFrame(); return; }
        if (GpuRecording.OpenOwner(_device) is { } owner)
            throw new InvalidOperationException($"Advance frame retirement before recording {owner}.");
        _frame++;
        FreeRipeBatches();
        int fresh = _pending.Count - _batched;
        if (fresh == 0) { _sharedDeferredBoundaries = 0; return; }
        if (!submission.TryRetain(this, out IGpuFence? completion))
        {
            // A resource retired before boundary M can only have readers recorded before M. A fence at a
            // later boundary completes after those readers too. Deferring the seal only extends retention.
            SharedSealDeferrals++;
            _sharedDeferredBoundaries++;
            if ((long)_batches.Count + _sharedDeferredBoundaries > MaxSealedBatches
                && _fallback == GpuRetireFallback.DrainDevice)
                DrainSharedHolding();
            FreeRipeBatches();
            return;
        }
        _sharedDeferredBoundaries = 0;
        _batches.Add(new Batch(fresh, completion, _frame - 1, sharedFence: true));
        _batched = _pending.Count;
        FreeRipeBatches();
    }

    void DrainSharedHolding()
    {
        // BeginFrame already proved no list is recording. The drain therefore covers every earlier reader,
        // including readers of the unsealed tail. Borrowed fences remain owned and reclaimed by the host.
        _drainDevice();
        ValveDrains++;
        for (int i = 0; i < _batches.Count; i++)
        {
            var batch = _batches[i];
            _batches[i] = new(batch.Count, batch.Fence, batch.MaxRetiredAt, batch.SharedFence, completionProved: true);
        }
        FreeRipeBatches();
        // Disposal can retire children. Walk the growing tail just as FlushAll does.
        for (int i = 0; i < _pending.Count; i++) _pending[i].Dispose();
        _pending.Clear();
        _batched = _sharedDeferredBoundaries = 0;
    }

    bool IGpuFrameCompletionConsumer.HasPending(IGpuFence completion)
    {
        // Reclaim before the host considers resetting its fence. This also lets a one-slot host make progress.
        FreeRipeBatches();
        return FirstSharedBatch(completion) >= 0;
    }

    void IGpuFrameCompletionConsumer.DiscardUnsubmitted(IGpuFence completion)
    {
        int first = FirstSharedBatch(completion);
        if (first < 0) return;
        // Abandonment is no completion proof for earlier submitted readers. Unseal the tail for the next host.
        for (int i = first; i < _batches.Count; i++)
            if (!_batches[i].SharedFence || !ReferenceEquals(_batches[i].Fence, completion))
                throw new InvalidOperationException("An abandoned retirement batch must be the submission tail.");
        for (int i = first; i < _batches.Count; i++) _batched -= _batches[i].Count;
        _batches.RemoveRange(first, _batches.Count - first);
    }

    void IGpuFrameCompletionConsumer.ReleaseCompleted(IGpuFence completion)
    {
        int last = -1;
        for (int i = 0; i < _batches.Count; i++)
            if (_batches[i].SharedFence && ReferenceEquals(_batches[i].Fence, completion)) last = i;
        // The host's completion or successful teardown drain proves this prefix, even if the backend has not
        // published its fence bit yet. Never return a borrowed host fence to the private barrier's pool.
        for (int i = 0; i <= last; i++)
        {
            Batch batch = _batches[i];
            _batches[i] = new Batch(batch.Count, batch.Fence, batch.MaxRetiredAt, batch.SharedFence, completionProved: true);
        }
        FreeRipeBatches();
    }

    int FirstSharedBatch(IGpuFence completion)
    {
        for (int i = 0; i < _batches.Count; i++)
            if (_batches[i].SharedFence && ReferenceEquals(_batches[i].Fence, completion)) return i;
        return -1;
    }
}
