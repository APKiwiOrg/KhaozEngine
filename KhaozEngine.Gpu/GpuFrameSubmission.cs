using System;
using System.Runtime.ExceptionServices;

namespace KhaozEngine.Gpu;

/// <summary>Single-threaded frame submission and fixed completion retention. Ordinary paths only poll.
/// Dispose before the host command list and device. Dispose performs the sole host teardown drain.</summary>
public sealed class GpuFrameSubmission : IDisposable
{
    readonly IGpuDevice device;
    readonly Slot[] slots;
    readonly IGpuFrameSubmissionParticipant?[] participants;
    int participantCount, current = -1;
    bool recording, disposed;

    public GpuFrameSubmission(IGpuDevice device, int retainedFrames = 3, int consumerCapacity = 8)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consumerCapacity);
        this.device = device;
        participants = new IGpuFrameSubmissionParticipant[consumerCapacity];
        slots = new Slot[device.Capabilities.SupportsCompletionFences ? retainedFrames : 0];
        int created = 0;
        try
        {
            for (; created < slots.Length; created++) slots[created] = new(device.Factory.CreateFence(), consumerCapacity);
        }
        catch
        {
            for (int i = 0; i < created; i++) slots[i].Fence.Dispose();
            throw;
        }
    }

    /// <summary>Abandon a prior unsubmitted recording, then reclaim only signaled frames with no consumers.</summary>
    public void Begin()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Abandon();
        foreach (var slot in slots)
        {
            if (!slot.Submitted || !slot.Fence.Signaled) continue;
            bool pending = false;
            for (int i = 0; i < slot.Count; i++) pending |= slot.Consumers[i]!.HasPending(slot.Fence);
            if (pending) continue;
            slot.Fence.Reset(); slot.Clear();
        }
        recording = true;
    }

    /// <summary>Register once per recording. Full participant capacity returns false independently of fences.</summary>
    public bool TryRegister(IGpuFrameSubmissionParticipant participant)
    {
        RequireRecording(); ArgumentNullException.ThrowIfNull(participant);
        for (int i = 0; i < participantCount; i++) if (ReferenceEquals(participants[i], participant)) return true;
        if (participantCount == participants.Length) return false;
        participants[participantCount++] = participant;
        return true;
    }

    /// <summary>Retain the recording's shared fence, or return false without blocking or allocating.</summary>
    public bool TryRetain(IGpuFrameCompletionConsumer consumer, out IGpuFence? completion)
    {
        RequireRecording(); ArgumentNullException.ThrowIfNull(consumer);
        completion = null;
        if (current < 0)
            for (int i = 0; i < slots.Length; i++)
                if (!slots[i].Submitted) { current = i; break; }
        if (current < 0) return false;
        var slot = slots[current];
        for (int i = 0; i < slot.Count; i++)
            if (ReferenceEquals(slot.Consumers[i], consumer)) { completion = slot.Fence; return true; }
        if (slot.Count == slot.Consumers.Length) return false;
        slot.Consumers[slot.Count++] = consumer;
        completion = slot.Fence;
        return true;
    }

    /// <summary>Submit this list exactly once, then notify every participant, even if a callback throws.
    /// A submission exception quarantines retained copies until completion or host teardown.</summary>
    public void Submit(IGpuCommandList commands)
    {
        RequireRecording(); ArgumentNullException.ThrowIfNull(commands);
        recording = false;
        var slot = current < 0 ? null : slots[current];
        if (slot is not null) slot.Submitted = true;
        current = -1;
        try
        {
            if (slot is null) device.Submit(commands);
            else device.Submit(commands, slot.Fence);
        }
        catch
        {
            Notify(success: false);
            throw;
        }
        Notify(success: true)?.Throw();
    }

    /// <summary>Discard only the current unsubmitted recording. Already submitted copies remain retained.</summary>
    public void Abandon()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!recording) return;
        recording = false;
        ExceptionDispatchInfo? error = Notify(success: false);
        if (current >= 0)
        {
            var slot = slots[current];
            for (int i = 0; i < slot.Count; i++)
                try { slot.Consumers[i]!.DiscardUnsubmitted(slot.Fence); }
                catch (Exception failure) { error ??= ExceptionDispatchInfo.Capture(failure); }
            // Keep the slot if a broken consumer refused to release it.
            bool pending = false;
            for (int i = 0; i < slot.Count; i++) pending |= slot.Consumers[i]!.HasPending(slot.Fence);
            if (pending) slot.Submitted = true;
            else slot.Clear();
            current = -1;
        }
        error?.Throw();
    }

    public void Dispose()
    {
        if (disposed) return;
        if (GpuRecording.OpenOwner(device) is { } owner) throw new GpuDrainDuringRecordingException(owner);
        ExceptionDispatchInfo? error = null;
        try { Abandon(); } catch (Exception failure) { error = ExceptionDispatchInfo.Capture(failure); }
        // Host teardown is the only blocking boundary. It also covers a submission that threw after replay.
        device.WaitForIdle();
        foreach (var slot in slots)
        {
            for (int i = 0; i < slot.Count; i++) slot.Consumers[i]!.ReleaseCompleted(slot.Fence);
            for (int i = 0; i < slot.Count; i++)
                if (slot.Consumers[i]!.HasPending(slot.Fence))
                    throw new InvalidOperationException("A completion consumer retained staging after host teardown.");
        }
        foreach (var slot in slots) slot.Fence.Dispose();
        disposed = true;
        error?.Throw();
    }

    void RequireRecording()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!recording) throw new InvalidOperationException("Begin a frame submission before recording.");
    }

    ExceptionDispatchInfo? Notify(bool success)
    {
        ExceptionDispatchInfo? error = null;
        int count = participantCount; participantCount = 0;
        for (int i = 0; i < count; i++)
        {
            var participant = participants[i]!; participants[i] = null;
            try { if (success) participant.Submitted(); else participant.Abandoned(); }
            catch (Exception failure) { error ??= ExceptionDispatchInfo.Capture(failure); }
        }
        return error;
    }

    sealed class Slot(IGpuFence fence, int capacity)
    {
        internal readonly IGpuFence Fence = fence;
        internal readonly IGpuFrameCompletionConsumer?[] Consumers = new IGpuFrameCompletionConsumer[capacity];
        internal int Count;
        internal bool Submitted;
        internal void Clear() { Array.Clear(Consumers); Count = 0; Submitted = false; }
    }
}
