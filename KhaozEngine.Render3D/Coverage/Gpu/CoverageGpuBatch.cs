using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>Cold-reserved coverage storage and an allocation-free actor frame directory.</summary>
internal sealed partial class CoverageGpuBatch : IDisposable
{
    readonly IGpuDevice device;
    CoverageGpuReservation[] actors = [];
    uint[] frameWords = [];
    IGpuBuffer? staticData, frameData, work, current, previous, results;
    IGpuResourceSet? resources, reverseResources;
    bool reversed;
    int historyOwners;
    bool retired;
    bool recording, disposed;

    internal CoverageGpuBatch(IGpuDevice device) => this.device = device ?? throw new ArgumentNullException(nameof(device));
    internal int Count { get; private set; }
    internal Action<IGpuCommandList, CoverageGpuPhase, bool>? DispatchObserverForTests { get; set; }
    internal ReadOnlySpan<CoverageGpuReservation> Actors => actors.AsSpan(0, Count);
    internal IGpuBuffer StaticData => staticData ?? throw new InvalidOperationException("Reserve coverage storage first.");
    internal IGpuBuffer FrameData => frameData ?? throw new InvalidOperationException("Reserve coverage storage first.");
    internal IGpuBuffer Work => work ?? throw new InvalidOperationException("Reserve coverage storage first.");
    internal IGpuBuffer Current => (reversed ? previous : current) ?? throw new InvalidOperationException("Reserve coverage storage first.");
    internal IGpuBuffer Previous => (reversed ? current : previous) ?? throw new InvalidOperationException("Reserve coverage storage first.");
    internal IGpuBuffer Results => results ?? throw new InvalidOperationException("Reserve coverage storage first.");

    // The frame buffer starts with actor-capacity frame offsets. Actor headers themselves retain the pinned ABI.
    internal void Reserve(int actors, int frameWords, int workWords, int surfaceWords)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (recording) throw new InvalidOperationException("Coverage storage cannot grow after recording starts.");
        ArgumentOutOfRangeException.ThrowIfNegative(actors);
        ArgumentOutOfRangeException.ThrowIfNegative(frameWords);
        ArgumentOutOfRangeException.ThrowIfNegative(workWords);
        ArgumentOutOfRangeException.ThrowIfNegative(surfaceWords);
        if (actors > this.actors.Length) Array.Resize(ref this.actors, actors);
        int frames = checked(frameWords + this.actors.Length);
        if (frames > this.frameWords.Length) Array.Resize(ref this.frameWords, frames);
        Grow(ref frameData, this.frameWords.Length, GpuBufferUsage.StructuredBufferReadOnly);
        Grow(ref work, workWords, GpuBufferUsage.StructuredBufferReadWrite);
        Grow(ref current, surfaceWords, GpuBufferUsage.StructuredBufferReadWrite);
        Grow(ref previous, surfaceWords, GpuBufferUsage.StructuredBufferReadWrite);
        Grow(ref results, checked(this.actors.Length * CoverageGpuLayout.FinalHeaderWords), GpuBufferUsage.StructuredBufferReadWrite);
    }

    internal void UploadStatic(ReadOnlySpan<uint> words)
    {
        if (recording) throw new InvalidOperationException("Static coverage data is immutable after recording.");
        Grow(ref staticData, words.Length, GpuBufferUsage.StructuredBufferReadOnly);
        if (!words.IsEmpty) device.UpdateBuffer(StaticData, 0, words);
    }

    internal void BeginFrame()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Count = 0;
        Array.Clear(frameWords);
    }

    internal void Add(CoverageGpuReservation actor, ReadOnlySpan<uint> frame)
    {
        if (actor.ActorSlot != Count || Count >= actors.Length || actor.FrameBase < actors.Length
            || (long)actor.FrameBase + frame.Length > frameWords.Length)
            throw new ArgumentException("Coverage actor must fit its reserved frame and consecutive slot.", nameof(actor));
        if (frame.Length < CoverageGpuLayout.ActorHeaderWords || frame[0] != actor.StaticBase || frame[1] != actor.WorkBase
            || frame[2] != actor.CurrentBase || frame[3] != actor.PreviousBase || frame[4] != actor.ActorSlot * 16u
            || frame[5] != actor.SourceCount || frame[6] != actor.VertexCount || frame[7] != actor.TriangleCount
            || frame.Length != CoverageGpuLayout.FrameWords(checked((int)frame[11])))
            throw new ArgumentException("Coverage frame does not match its reservation.", nameof(frame));
        actors[Count] = actor; frameWords[Count] = actor.FrameBase;
        frame.CopyTo(frameWords.AsSpan((int)actor.FrameBase)); Count++;
    }

    internal void UploadFrame() => device.UpdateBuffer(FrameData, 0, frameWords);
    internal void UploadFrame(IGpuCommandList commands) => commands.UpdateBuffer(FrameData, 0, frameWords);

    internal uint Elements(CoverageGpuPhase phase)
    {
        uint maximum = 1;
        foreach (var actor in Actors)
        {
            uint f = actor.FrameBase;
            uint count = phase switch
            {
                CoverageGpuPhase.Reset => Math.Max(frameWords[f + 5], Math.Max(frameWords[f + 9],
                    Math.Max(frameWords[f + 6], Math.Max(checked(frameWords[f + 7] * 3), checked(frameWords[f + 8] * 2))))),
                CoverageGpuPhase.Skin or CoverageGpuPhase.ValidateSource => frameWords[f + 5],
                CoverageGpuPhase.ValidateAliases => frameWords[f + 9],
                CoverageGpuPhase.InterpolateGroups or CoverageGpuPhase.ConformGroups or CoverageGpuPhase.GatherCorrections
                    or CoverageGpuPhase.ConformBoundary or CoverageGpuPhase.ConformResume
                    or CoverageGpuPhase.CheckFullTargets or CoverageGpuPhase.TransitionGroups => frameWords[f + 8],
                CoverageGpuPhase.CorrectFaces or CoverageGpuPhase.CorrectFacesOverlay or CoverageGpuPhase.CorrectFacesOverlayReplay or CoverageGpuPhase.CorrectFacesFinish
                    or CoverageGpuPhase.PrepareNormalBasis or CoverageGpuPhase.FinishNormalBasis or CoverageGpuPhase.ValidateFaces => frameWords[f + 7],
                CoverageGpuPhase.Select => Math.Max(frameWords[f + 5], frameWords[f + 6]),
                _ => frameWords[f + 6]
            };
            maximum = Math.Max(maximum, count);
        }
        return maximum;
    }

    internal void Prepare(IGpuResourceLayout layout)
    {
        if (recording) throw new InvalidOperationException("Coverage bindings must be prepared before recording.");
        if (resources is not null) return;
        var forward = device.Factory.CreateResourceSet(new(layout, StaticData, FrameData, Work, current!, previous!, Results));
        try { reverseResources = device.Factory.CreateResourceSet(new(layout, StaticData, FrameData, Work, previous!, current!, Results)); }
        catch { forward.Dispose(); throw; }
        resources = forward;
    }

    internal IGpuResourceSet Bind(IGpuResourceLayout layout)
    {
        if (resources is null) Prepare(layout);
        recording = true;
        return (reversed ? reverseResources : resources) ?? throw new InvalidOperationException("Prepare coverage bindings before recording.");
    }

    void Grow(ref IGpuBuffer? buffer, int words, GpuBufferUsage usage)
    {
        uint bytes = CoverageGpuLayout.Bytes(CoverageGpuLayout.Words(Math.Max(1, words)));
        if (buffer != null && buffer.SizeInBytes >= bytes) return;
        buffer?.Dispose();
        buffer = device.Factory.CreateBuffer(new(bytes, usage, 4));
    }

    internal void CommitFinals() => reversed = !reversed;
    internal void RetainHistory() => historyOwners++;
    internal void ReleaseHistory()
    {
        if (--historyOwners == 0 && retired) Dispose();
    }

    // The submission owner retires this batch only after its final submitted use completes.
    public void Dispose()
    {
        retired = true;
        if (disposed || historyOwners != 0) return;
        disposed = true;
        resources?.Dispose(); reverseResources?.Dispose();
        staticData?.Dispose(); frameData?.Dispose(); work?.Dispose();
        current?.Dispose(); previous?.Dispose(); results?.Dispose();
    }
}
