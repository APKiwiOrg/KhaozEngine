using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

internal readonly record struct CoverageOutcomeCommand(ulong Equipment, uint Command);

// Values captured before submission. Never retain a mutable preparation or conformer here.
internal readonly record struct CoverageOutcomeIdentity(ulong FrameId, ulong ViewId, MotionKey Actor,
    ulong Preparation, string Host)
{
    internal CoverageOutcomeCommand Command0 { get; init; }
    internal CoverageOutcomeCommand Command1 { get; init; }
    internal CoverageOutcomeCommand Command2 { get; init; }
    internal CoverageOutcomeCommand Command3 { get; init; }
    internal bool Accepts(ulong equipment, uint command) => equipment == 0 && command == 0
        || Command0 == new CoverageOutcomeCommand(equipment, command)
        || Command1 == new CoverageOutcomeCommand(equipment, command)
        || Command2 == new CoverageOutcomeCommand(equipment, command)
        || Command3 == new CoverageOutcomeCommand(equipment, command);
}

/// <summary>One cold capacity generation, retained across scene growth and deferred scene disposal.</summary>
internal sealed class CoverageOutcomeReadbacks : IGpuFrameCompletionConsumer, IGpuFrameSubmissionParticipant, IDisposable
{
    readonly GpuBufferReadbackRing ring;
    readonly RetainedFrame[] frames = new RetainedFrame[3];
    readonly byte[] payload;
    int active = -1, actor, recording = -1;
    ulong sequence;
    bool disposalRequested, disposed;
    internal CoverageOutcomeReadbacks? Next;
    internal int Capacity { get; }
    internal bool HasOutcomes => active >= 0 || ring.PendingCount != 0;

    internal CoverageOutcomeReadbacks(IGpuDevice device, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        payload = new byte[checked(capacity * 64)];
        for (int i = 0; i < frames.Length; i++) frames[i] = new(capacity);
        ring = new(device, 3, checked((uint)payload.Length));
    }

    internal bool Record(IGpuCommandList commands, GpuFrameSubmission submission, IGpuBuffer results,
        ReadOnlySpan<CoverageOutcomeIdentity> identities)
    {
        ObjectDisposedException.ThrowIf(disposalRequested, this);
        if (identities.Length > Capacity) throw new ArgumentOutOfRangeException(nameof(identities));
        if (identities.IsEmpty) return false;
        int slot = 0;
        while (slot < frames.Length && frames[slot].Count != 0) slot++;
        if (slot == frames.Length || !submission.TryRetain(this, out var completion)) return false;
        if (!ring.TryRecord(commands, results, 0, checked((uint)identities.Length * 64), identities[0].FrameId, completion!)) return false;
        identities.CopyTo(frames[slot].Identities);
        frames[slot].Count = identities.Length;
        frames[slot].Completion = completion;
        frames[slot].Order = ++sequence;
        recording = slot;
        return true;
    }

    internal bool TryRead(out CoverageGpuOutcome outcome)
    {
        outcome = default;
        if (disposalRequested) return false;
        if (active < 0)
        {
            if (!ring.TryRead(payload, out ulong tag, out _)) return false;
            ulong order = ulong.MaxValue;
            for (int i = 0; i < frames.Length; i++)
                if (frames[i].Count > 0 && frames[i].Identities[0].FrameId == tag && frames[i].Order < order)
                { active = i; order = frames[i].Order; }
            if (active < 0) throw new InvalidOperationException("A completed coverage copy has no retained metadata.");
            actor = 0;
        }
        var frame = frames[active];
        var identity = frame.Identities[actor];
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(payload.AsSpan(actor * 64, 64));
        ulong equipment = Pair(words, 9);
        var code = (CoverageFailureCode)words[8];
        bool valid = Pair(words, 0) == identity.FrameId && Pair(words, 2) == identity.ViewId
            && Pair(words, 4) == identity.Actor.Value && Pair(words, 6) == identity.Preparation
            && code is >= CoverageFailureCode.None and <= CoverageFailureCode.UnsupportedAdapter
            && words[12] <= int.MaxValue && words[13] <= int.MaxValue
            && words[14] == (code == CoverageFailureCode.None ? 0u : 1u)
            && identity.Accepts(equipment, words[11]);
        CoverageFailure failure = !valid ? new(CoverageFailureCode.IdentityMismatch, identity.Host, 0, 0, 0, 0)
            : code == CoverageFailureCode.None ? default
            : new(code, identity.Host, equipment, words[11], (int)words[12], (int)words[13]);
        outcome = new(identity.FrameId, identity.ViewId, identity.Actor,
            new(failure.IsSuccess ? CoveragePresentation.Covered : CoveragePresentation.UncoveredFallback, failure));
        if (++actor == frame.Count) { frame.Clear(); active = -1; }
        return true;
    }

    public void Submitted()
    {
        if (recording >= 0) frames[recording].Submitted = true;
        recording = -1;
    }
    public void Abandoned() => recording = -1;

    public bool HasPending(IGpuFence completion)
    {
        if (disposalRequested && ring.HasPending(completion) && completion.Signaled) ReleaseCompleted(completion);
        return ring.HasPending(completion);
    }

    public void DiscardUnsubmitted(IGpuFence completion)
    {
        if (disposed) return;
        ring.DiscardUnsubmitted(completion);
        ClearFrames(completion);
        FinishDisposal();
    }

    public void ReleaseCompleted(IGpuFence completion)
    {
        if (disposed) return;
        ring.ReleaseCompleted(completion);
        ClearFrames(completion);
        FinishDisposal();
    }

    void ClearFrames(IGpuFence completion)
    {
        for (int i = 0; i < frames.Length; i++)
            if (i != active && ReferenceEquals(frames[i].Completion, completion)) frames[i].Clear();
    }

    /// <summary>Request cleanup without waiting. Submitted staging stays alive until its owner proves completion.</summary>
    public void Dispose()
    {
        if (disposalRequested) return;
        disposalRequested = true;
        if (active >= 0) { frames[active].Clear(); active = -1; }
        foreach (var frame in frames)
            if (frame.Submitted && frame.Completion is { } completion && completion.Signaled) ReleaseCompleted(completion);
        FinishDisposal();
    }

    void FinishDisposal()
    {
        if (!disposalRequested || disposed || ring.PendingCount != 0) return;
        ring.Dispose(); disposed = true;
    }

    static ulong Pair(ReadOnlySpan<uint> words, int start) => words[start] | (ulong)words[start + 1] << 32;

    sealed class RetainedFrame(int capacity)
    {
        internal readonly CoverageOutcomeIdentity[] Identities = new CoverageOutcomeIdentity[capacity];
        internal int Count;
        internal bool Submitted;
        internal ulong Order;
        internal IGpuFence? Completion;
        internal void Clear() { Array.Clear(Identities, 0, Count); Count = 0; Completion = null; Submitted = false; }
    }
}
