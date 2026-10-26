using System;

namespace KhaozEngine.Gpu;

public sealed partial class GpuRetireQueue : IGpuFrameCompletionConsumer
{
    /// <summary>Advance before recording using this host frame's completion fence instead of an empty submission.
    /// The host must submit before the next boundary and outlive this queue, or complete its teardown first.
    /// No completion capacity refuses without submitting or freeing the unsealed tail. Devices without completion
    /// fences retain the ordinary frame-count and drain fallback.</summary>
    public void BeginFrame(GpuFrameSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (!ReferenceEquals(_device, submission.Device))
            throw new ArgumentException("The retirement queue and frame host must use the same device.", nameof(submission));
        if (!_device!.Capabilities.SupportsCompletionFences) { BeginFrame(); return; }
        if (GpuRecording.OpenOwner(_device) is { } owner)
            throw new InvalidOperationException($"Advance frame retirement before recording {owner}.");
        _frame++;
        FreeRipeBatches();
        int fresh = _pending.Count - _batched;
        if (fresh == 0) return;
        if (!submission.TryRetain(this, out IGpuFence? completion))
            throw new InvalidOperationException("The frame host has no completion capacity for GPU retirement.");
        _batches.Add(new Batch(fresh, completion, _frame - 1, sharedFence: true));
        _batched = _pending.Count;
        FreeRipeBatches();
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
