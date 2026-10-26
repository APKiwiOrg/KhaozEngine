using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace KhaozEngine.Gpu;

/// <summary>Single-threaded frame submission and fixed completion retention. Ordinary paths only poll.
/// Dispose before the host command list and device. Dispose performs the sole host teardown drain.</summary>
public sealed class GpuFrameSubmission : IDisposable
{
    readonly IGpuDevice device;
    readonly Slot[] slots;
    readonly IGpuFrameSubmissionParticipant?[] participants;
    IGpuFence[] submitCompletions;
    int participantCount, current = -1;
    bool recording, disposed, teardown;
    internal bool TeardownCompleted => disposed;
    internal IGpuDevice Device => device;

    /// <summary>Preallocate fixed frame fences and consumer/participant storage on the owning host thread.
    /// Devices without completion fences retain participant submission but cannot retain consumers.</summary>
    public GpuFrameSubmission(IGpuDevice device, int retainedFrames = 3, int consumerCapacity = 8)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consumerCapacity);
        this.device = device;
        participants = new IGpuFrameSubmissionParticipant[consumerCapacity];
        submitCompletions = new IGpuFence[consumerCapacity + 1];
        slots = new Slot[device.Capabilities.SupportsCompletionFences ? retainedFrames : 0];
        int created = 0;
        try
        {
            for (; created < slots.Length; created++) slots[created] = new(device.Factory.CreateFence(), consumerCapacity);
        }
        catch (Exception error)
        {
            var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
            GpuDiagnosticFailureException.Append(failures, "renderer.session.construct", error);
            for (int i = 0; i < created; i++)
                try { slots[i].Fence.Dispose(); }
                catch (Exception cleanup) { GpuDiagnosticFailureException.Append(failures, "renderer.session.dispose.submission.fence." + i, cleanup); }
            if (failures.Count > 1) throw new GpuDiagnosticFailureException(failures.ToImmutable());
            throw;
        }
    }

    /// <summary>Abandon a prior unsubmitted recording, then reclaim only signaled frames with no consumers.</summary>
    public void Begin()
    {
        ObjectDisposedException.ThrowIf(disposed || teardown, this);
        ImmutableArray<GpuDiagnosticFailure>.Builder? failures = null;
        try { Abandon(); }
        catch (Exception error) { Capture(ref failures, "renderer.frame.submission.abandon", error); }
        for (int ordinal = 0; ordinal < slots.Length; ordinal++)
        {
            var slot = slots[ordinal];
            if (!slot.Retained) continue;
            try
            {
                if (!slot.Fence.Signaled || HasPending(slot, ordinal, ref failures)) continue;
                slot.Fence.Reset(); slot.Clear();
            }
            catch (Exception failure) { Capture(ref failures, "renderer.frame.submission.reclaim." + ordinal, failure); }
        }
        if (failures is not null) throw new GpuDiagnosticFailureException(failures.ToImmutable());
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
    public bool TryRetain(IGpuFrameCompletionConsumer consumer, [NotNullWhen(true)] out IGpuFence? completion)
    {
        RequireRecording(); ArgumentNullException.ThrowIfNull(consumer);
        completion = null;
        if (current < 0)
            for (int i = 0; i < slots.Length; i++)
                if (!slots[i].Retained) { current = i; break; }
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
    public void Submit(IGpuCommandList commands) => Submit(commands, ReadOnlySpan<IGpuFence>.Empty);

    public void Submit(IGpuCommandList commands, ReadOnlySpan<IGpuFence> completions)
    {
        RequireRecording(); ArgumentNullException.ThrowIfNull(commands);
        recording = false;
        var slot = current < 0 ? null : slots[current];
        if (slot is not null) slot.Retained = true;
        current = -1;
        try
        {
            if (slot is null) device.Submit(commands, completions);
            else
            {
                int count = checked(completions.Length + 1);
                if (submitCompletions.Length < count) Array.Resize(ref submitCompletions, count);
                submitCompletions[0] = slot.Fence;
                completions.CopyTo(submitCompletions.AsSpan(1));
                try { device.Submit(commands, submitCompletions.AsSpan(0, count)); }
                finally { Array.Clear(submitCompletions, 0, count); }
            }
        }
        catch (Exception error)
        {
            var notification = Notify(success: false);
            if (notification is not null)
            {
                var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
                if (error is GpuDiagnosticFailureException original) failures.AddRange(original.Failures);
                else failures.Add(new("renderer.frame", error));
                if (notification.SourceException is GpuDiagnosticFailureException cleanup) failures.AddRange(cleanup.Failures);
                else failures.Add(new("renderer.frame.submission.recovery", notification.SourceException));
                throw new GpuDiagnosticFailureException(failures.ToImmutable());
            }
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
        int ordinal = current;
        var slot = ordinal < 0 ? null : slots[ordinal];
        current = -1;
        // Quarantine before any external callback or query can throw or reenter the owner.
        if (slot is not null) slot.Retained = true;
        ImmutableArray<GpuDiagnosticFailure>.Builder? failures = null;
        if (Notify(success: false) is { } notification)
            Capture(ref failures, "renderer.frame.submission.recovery", notification.SourceException);
        if (slot is not null)
        {
            for (int i = 0; i < slot.Count; i++)
                try { slot.Consumers[i]!.DiscardUnsubmitted(slot.Fence); }
                catch (Exception failure) { Capture(ref failures, "renderer.frame.submission.discard." + ordinal + "." + i, failure); }
            if (!HasPending(slot, ordinal, ref failures)) slot.Clear();
        }
        if (failures is not null) throw new GpuDiagnosticFailureException(failures.ToImmutable());
    }

    /// <summary>Abandon the open recording, drain submitted work at host teardown, release consumers and dispose
    /// owned fences. Dispose before the host command list and device. Failed teardown may be retried.</summary>
    public void Dispose()
    {
        if (disposed) return;
        if (GpuRecording.OpenOwner(device) is { } owner) throw new GpuDrainDuringRecordingException(owner);
        teardown = true;
        var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        void Capture(string stage, Exception error)
        {
            if (error is GpuDiagnosticFailureException transport) failures.AddRange(transport.Failures);
            else failures.Add(new(stage, error));
        }
        void ThrowFailures()
        {
            if (failures.Count != 0) throw new GpuDiagnosticFailureException(failures.ToImmutable());
        }
        try { Abandon(); } catch (Exception error) { Capture("renderer.session.dispose.submission.abandon", error); }
        try { device.WaitForIdle(); }
        catch (Exception error)
        {
            Capture("renderer.session.dispose.submission.drain", error);
            ThrowFailures();
        }
        // A failed drain never reaches a child release, including fences and quarantined consumer storage.
        bool pending = false;
        for (int ordinal = 0; ordinal < slots.Length; ordinal++)
        {
            var slot = slots[ordinal];
            for (int i = 0; i < slot.Count; i++)
            {
                try { slot.Consumers[i]!.ReleaseCompleted(slot.Fence); }
                catch (Exception error) { Capture("renderer.session.dispose.submission.consumer." + ordinal + "." + i, error); }
                try { pending |= slot.Consumers[i]!.HasPending(slot.Fence); }
                catch (Exception error) { pending = true; Capture("renderer.session.dispose.submission.pending." + ordinal + "." + i, error); }
            }
        }
        if (pending)
        {
            if (failures.Count == 0) Capture("renderer.session.dispose.submission.pending",
                new InvalidOperationException("A completion consumer retained staging after host teardown."));
            ThrowFailures();
        }
        for (int ordinal = 0; ordinal < slots.Length; ordinal++)
            try { slots[ordinal].Fence.Dispose(); }
            catch (Exception error) { Capture("renderer.session.dispose.submission.fence." + ordinal, error); }
        disposed = true;
        ThrowFailures();
    }

    void RequireRecording()
    {
        ObjectDisposedException.ThrowIf(disposed || teardown, this);
        if (!recording) throw new InvalidOperationException("Begin a frame submission before recording.");
    }

    ExceptionDispatchInfo? Notify(bool success)
    {
        ImmutableArray<GpuDiagnosticFailure>.Builder? failures = null;
        int count = participantCount; participantCount = 0;
        for (int i = 0; i < count; i++)
        {
            var participant = participants[i]!; participants[i] = null;
            try { if (success) participant.Submitted(); else participant.Abandoned(); }
            catch (Exception failure)
            {
                failures ??= ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
                if (failure is GpuDiagnosticFailureException transport) failures.AddRange(transport.Failures);
                else failures.Add(new("renderer.frame.submission.participant." + i, failure));
            }
        }
        return failures is null ? null : ExceptionDispatchInfo.Capture(new GpuDiagnosticFailureException(failures.ToImmutable()));
    }

    static void Capture(ref ImmutableArray<GpuDiagnosticFailure>.Builder? failures, string stage, Exception error)
    {
        failures ??= ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        GpuDiagnosticFailureException.Append(failures, stage, error);
    }

    static bool HasPending(Slot slot, int ordinal, ref ImmutableArray<GpuDiagnosticFailure>.Builder? failures)
    {
        bool pending = false;
        for (int i = 0; i < slot.Count; i++)
            try { pending |= slot.Consumers[i]!.HasPending(slot.Fence); }
            catch (Exception failure)
            {
                pending = true;
                Capture(ref failures, "renderer.frame.submission.pending." + ordinal + "." + i, failure);
            }
        return pending;
    }

    sealed class Slot(IGpuFence fence, int capacity)
    {
        internal readonly IGpuFence Fence = fence;
        internal readonly IGpuFrameCompletionConsumer?[] Consumers = new IGpuFrameCompletionConsumer[capacity];
        internal int Count;
        internal bool Retained;
        internal void Clear() { Array.Clear(Consumers); Count = 0; Retained = false; }
    }
}
