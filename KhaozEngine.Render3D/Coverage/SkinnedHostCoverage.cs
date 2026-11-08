using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Actor-owned table carry preparation over a complete immutable uploaded host. Use on the scene thread.</summary>
public sealed class SkinnedHostCoverage : IDisposable
{
    internal readonly Scene3D Scene;
    internal readonly SkinnedMeshHandle[] Meshes;
    internal readonly Matrix4x4[] InverseBind;
    internal readonly CoverageTableBinding Binding;
    internal readonly CoverageCarryUniforms Uniforms;
    internal readonly CoverageActorBounds Bounds;
    internal readonly CoveragePreparation Request;
    internal readonly CoverageEquipmentStage[] Equipment = new CoverageEquipmentStage[CoverageCarryUniforms.MaxCommands];
    internal MotionKey Actor;
    internal bool Disposed;
    readonly int thread = Environment.CurrentManagedThreadId;

    internal SkinnedHostCoverage(Scene3D scene, SkinnedMeshHandle[] meshes, CoverageRefinement topology,
        Matrix4x4[] inverseBind, CoverageTableBinding binding)
    {
        Scene = scene; Meshes = meshes; Topology = topology; InverseBind = inverseBind; Binding = binding;
        Uniforms = new(topology, inverseBind.Length);
        Bounds = new(topology, binding.Table);
        Request = new(this, inverseBind.Length, meshes.Length);
        for (int i = 0; i < Equipment.Length; i++) Equipment[i] = new(this);
    }
    public CoverageRefinement Topology { get; }

    /// <summary>Whether this owner has an accepted preparation in the scene's current frame. This read-only
    /// check lets attachment binding refuse a repeat before changing borrowed descriptors or equipment stages.</summary>
    public bool IsPreparedThisFrame
    { get { RequireLive(); return Scene.IsCoveragePreparedThisFrame(this); } }

    /// <summary>Register one immutable actor incarnation and its exact host handles, also used by token-free fallback.
    /// Registration does not record a presented surface or commit history.</summary>
    public void BindActor(MotionKey actorKey)
    {
        RequireLive();
        if (actorKey.IsNone) throw new ArgumentException("An actor needs a nonzero motion key.", nameof(actorKey));
        Scene.BindCoverageActor(this, actorKey);
    }

    public bool MatchesHostSkin(ReadOnlySpan<Matrix4x4> inverseBind)
    { RequireLive(); return inverseBind.SequenceEqual(InverseBind); }

    /// <summary>Cold compatibility check over authored equipment and complete command regions. Never rebinds the table.</summary>
    public bool MatchesConfiguration(IReadOnlyList<CoverageConfigurationItem> items, ReadOnlySpan<int> regionCommands)
    {
        RequireLive();
        return CoverageConfigurationIdentity.Compute(items, InverseBind) == Binding.Table.Identity.ConfigurationSha256
            && Binding.MatchesRepresentatives(regionCommands);
    }

    /// <summary>Reserve complete material snapshots at cold equip, before any frame preparation.</summary>
    public void ReserveEquipment(int materialCapacity)
    {
        RequireLive();
        ArgumentOutOfRangeException.ThrowIfNegative(materialCapacity);
        foreach (var stage in Equipment) stage.Reserve(materialCapacity);
    }

    /// <summary>Expire borrowed host and equipment tokens on successful replacement or resource disposal.</summary>
    public void Invalidate()
    {
        RequireThread();
        Request.Revision++;
        Scene.RegisterCoverageAttempt(this);
        // Keep an accepted frame's snapshot reserved until Begin, even after its token expires.
    }

    /// <summary>Snapshot current pose inputs and select an entry using only the consumer's opaque state key.</summary>
    public bool TryPrepare(ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model, CoverageStateKey stateKey,
        ReadOnlySpan<CoverageCommandPose> commands, MotionKey actorKey, out SkinnedCoverageFrame frame, out CoverageFailure failure)
    {
        RequireLive();
        return Scene.StageCoverage(this, palette, model, stateKey, commands, actorKey, out frame, out failure);
    }

    internal void RequireThread()
    {
        if (Environment.CurrentManagedThreadId != thread)
            throw new CoverageFrameStateException(new(CoverageFailureCode.StaleFrame, Topology.HostIdentity, 0, 0, 0, 0));
    }
    void RequireLive() { RequireThread(); ObjectDisposedException.ThrowIf(Disposed, this); }
    public void Dispose()
    {
        RequireThread();
        if (Disposed) return;
        Invalidate(); Disposed = true;
        Scene.RetireCoverage(this);
    }
}
