using System;
using System.Numerics;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>Actor-owned render-thread preparation over one complete uploaded host topology.</summary>
public sealed class SkinnedHostConformer : IDisposable
{
    internal readonly Scene3D Scene;
    internal readonly SkinnedMeshHandle[] Meshes;
    internal readonly IGpuBuffer[] Indices;
    internal readonly CoverageScratch Scratch;
    internal readonly CoveragePreparation Request;
    internal readonly Matrix4x4[] InverseBind;
    internal readonly int Thread = Environment.CurrentManagedThreadId;
    internal bool Disposed, Busy;
    internal MotionKey Actor;
    internal ulong MotionRevision;
    internal int MotionCommandCount;
    internal readonly CoverageCommandPose[] MotionCommands = new CoverageCommandPose[CoverageScratch.MaxCommands];

    internal SkinnedHostConformer(Scene3D scene, SkinnedMeshHandle[] meshes, CoverageRefinement topology,
        Matrix4x4[] inverseBind, IGpuBuffer[] indices)
    {
        Scene = scene; Meshes = meshes; Topology = topology; InverseBind = inverseBind; Indices = indices;
        Scratch = new(topology);
        Request = new(this, inverseBind.Length, meshes.Length);
    }
    public CoverageRefinement Topology { get; }

    /// <summary>Compare a captured host skin with the exact uploaded inverse binds owned by this conformer.</summary>
    /// <remarks>Checks matrix count and values. Exact Skeleton identity is checked separately by attachment binding.
    /// Does not retain the supplied span or expose owned matrices. Valid calls allocate no managed memory.</remarks>
    public bool MatchesHostSkin(ReadOnlySpan<Matrix4x4> inverseBind)
    {
        RequireThread();
        ObjectDisposedException.ThrowIf(Disposed, this);
        return inverseBind.SequenceEqual(InverseBind);
    }

    /// <summary>Stages frozen current-pose geometry. True means accepted for late resolution in this frame.</summary>
    public bool TryPrepare(ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        ReadOnlySpan<CoverageCommandPose> commands, MotionKey actorKey, CoverageFallback fallback,
        out SkinnedCoverageFrame frame, out CoverageFailure failure)
    {
        RequireThread();
        if (Disposed) throw new ObjectDisposedException(nameof(SkinnedHostConformer));
        if (Busy) throw new InvalidOperationException("Coverage preparation is not reentrant.");
        Busy = true;
        try { return Scene.StageCoverage(this, palette, model, commands, actorKey, fallback, out frame, out failure); }
        finally { Busy = false; }
    }
    internal void RequireThread()
    {
        if (Environment.CurrentManagedThreadId != Thread)
            throw new InvalidOperationException("Coverage must run on its owning render thread.");
    }
    public void Dispose()
    {
        RequireThread();
        if (Disposed) return;
        Disposed = true;
        Scene.RetireCoverage(this);
    }
}
