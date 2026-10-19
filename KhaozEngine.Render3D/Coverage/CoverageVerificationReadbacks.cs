using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>Opt-in cold storage for three sampled poses and their submitted physical surfaces.</summary>
internal sealed class CoverageVerificationReadbacks : IGpuFrameCompletionConsumer, IGpuFrameSubmissionParticipant, IDisposable
{
    readonly GpuBufferReadbackRing ring;
    readonly byte[] payload;
    readonly IGpuFence?[] completions = new IGpuFence?[3];
    readonly bool[] submitted = new bool[3];
    readonly CoverageReferenceSkin skin;
    readonly CoverageReferenceScratch scratch;
    readonly Matrix4x4[] composed;
    readonly ModelVertex[] posed, final;
    readonly uint[] referenceWords;
    int staged = -1, recording = -1;
    IGpuBuffer? surface;
    bool disposalRequested, disposed;
    internal readonly CoverageVerificationPose[] Snapshots = new CoverageVerificationPose[3];
    internal CoverageVerificationReadbacks? Next;
    internal CoverageRefinement Topology { get; }
    internal int PendingCount => ring.PendingCount;
    internal int ReferenceCalls { get; private set; }
    internal ReadOnlySpan<uint> CompletedSurfaceWords => MemoryMarshal.Cast<byte, uint>(payload.AsSpan());

    internal CoverageVerificationReadbacks(IGpuDevice device, CoverageRefinement topology, int bones)
    {
        Topology = topology;
        composed = new Matrix4x4[bones];
        skin = new(topology.Source, bones); scratch = new(topology);
        posed = new ModelVertex[topology.SourceVertexCount]; final = new ModelVertex[topology.VertexCount];
        referenceWords = new uint[Math.Max(posed.Length, final.Length) * 16];
        payload = new byte[checked((16 + (posed.Length + final.Length) * 16) * 4)];
        for (int i = 0; i < Snapshots.Length; i++) Snapshots[i] = new(topology, bones);
        ring = new(device, 3, (uint)payload.Length);
    }

    internal void BeginFrame() { staged = -1; surface = null; }

    internal bool Stage(CoveragePreparation p, long frameIndex, ReadOnlySpan<uint> upload, IGpuBuffer physicalSurface)
    {
        if (disposalRequested || staged >= 0 || !ReferenceEquals(p.Owner.Topology, Topology)) return false;
        int slot = Array.IndexOf(completions, null);
        if (slot < 0) return false;
        Snapshots[slot].Capture(p, frameIndex, upload);
        surface = physicalSurface; staged = slot;
        return true;
    }

    internal bool Record(IGpuCommandList commands, GpuFrameSubmission submission)
    {
        if (disposalRequested || staged < 0) return false;
        int slot = staged; staged = -1;
        var source = surface!; surface = null;
        if (!submission.TryRetain(this, out var completion)) return false;
        var pose = Snapshots[slot];
        if (!ring.TryRecord(commands, source, checked(pose.Reservation.CurrentBase * 4), (uint)payload.Length,
            (ulong)slot, completion!)) return false;
        completions[slot] = completion; recording = slot;
        return true;
    }

    internal bool TryRead(out CoverageVerificationComparison comparison)
    {
        comparison = default;
        if (disposalRequested || !ring.TryRead(payload, out ulong slot, out _)) return false;
        var pose = Snapshots[slot];
        completions[slot] = null; submitted[slot] = false;
        var words = MemoryMarshal.Cast<byte, uint>(payload.AsSpan());
        bool refused = words[14] != 0;
        if (Pair(words, 0) != pose.FrameId || Pair(words, 2) != pose.ViewId || Pair(words, 4) != pose.Actor.Value
            || Pair(words, 6) != pose.Preparation || words[14] > 1)
        {
            comparison = CoverageVerificationComparison.Failure(pose, refused, false, default, "Identity", 0, 1);
            return true;
        }
        ReferenceCalls++;
        CoverageReferenceSkinning.Compose(pose.Palette, pose.InverseBind, composed);
        var paletteWords = MemoryMarshal.Cast<Matrix4x4, uint>(composed.AsSpan());
        var uploadedPalette = pose.UploadWords.AsSpan(16, paletteWords.Length);
        for (int word = 0; word < paletteWords.Length; word++)
            if (paletteWords[word] != uploadedPalette[word])
            {
                comparison = CoverageVerificationComparison.Failure(pose, refused, false, default,
                    "UploadedPalette", paletteWords[word], uploadedPalette[word]);
                return true;
            }
        // The retained upload is the input truth. Compose above checks original-pose provenance only.
        MemoryMarshal.Cast<uint, Matrix4x4>(uploadedPalette).CopyTo(composed);
        scratch.Arithmetic.Reset();
        bool skinned = CoverageReferenceSkinning.TrySkin(scratch.Arithmetic, skin, Topology.Source, composed, posed, out var code);
        bool accepted = false;
        CoverageFailure failure = default;
        if (skinned) accepted = CoverageReferenceConstructor.TryConstruct(Topology, posed, pose.Model,
            pose.Commands.AsSpan(0, pose.CommandCount), scratch, final, out failure);
        if (skinned) code = failure.Code;
        if (!skinned || !refused && !accepted)
        {
            comparison = CoverageVerificationComparison.Failure(pose, refused, accepted, code, "ReferenceOutcome", 0, (uint)code);
            return true;
        }
        ReadOnlySpan<ModelVertex> selected = refused ? posed : final;
        for (int v = 0; v < selected.Length; v++) CoverageGpuUpload.WriteModel(selected[v], referenceWords.AsSpan(v * 16));
        int offset = 16 + (refused ? 0 : posed.Length * 16);
        comparison = CoverageVerificationComparison.Compare(pose, refused, accepted, code,
            referenceWords.AsSpan(0, selected.Length * 16), words.Slice(offset, selected.Length * 16));
        return true;
    }

    public void Submitted()
    {
        if (recording >= 0) submitted[recording] = true;
        recording = -1;
    }
    public void Abandoned() { staged = recording = -1; surface = null; }
    public bool HasPending(IGpuFence completion)
    {
        if (disposalRequested && ring.HasPending(completion) && completion.Signaled) ReleaseCompleted(completion);
        return ring.HasPending(completion);
    }
    public void DiscardUnsubmitted(IGpuFence completion)
    {
        if (disposed) return;
        ring.DiscardUnsubmitted(completion); Clear(completion); FinishDisposal();
    }
    public void ReleaseCompleted(IGpuFence completion)
    {
        if (disposed) return;
        ring.ReleaseCompleted(completion); Clear(completion); FinishDisposal();
    }
    void Clear(IGpuFence completion)
    {
        for (int i = 0; i < completions.Length; i++)
            if (ReferenceEquals(completions[i], completion)) { completions[i] = null; submitted[i] = false; }
    }
    public void Dispose()
    {
        if (disposalRequested) return;
        disposalRequested = true; staged = -1; surface = null;
        for (int i = 0; i < completions.Length; i++)
            if (submitted[i] && completions[i] is { Signaled: true } completion) ReleaseCompleted(completion);
        FinishDisposal();
    }
    void FinishDisposal()
    {
        if (!disposalRequested || disposed || ring.PendingCount != 0) return;
        ring.Dispose(); disposed = true;
    }
    static ulong Pair(ReadOnlySpan<uint> words, int start) => words[start] | (ulong)words[start + 1] << 32;
}
