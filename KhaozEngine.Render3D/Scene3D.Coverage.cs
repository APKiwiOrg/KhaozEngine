using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    static int nextCoverageSceneStamp;
    readonly int _coverageSceneStamp = AllocateCoverageSceneStamp();
    readonly int _coverageThread = Environment.CurrentManagedThreadId;
    readonly List<SkinnedHostCoverage> _coverageHosts = [];
    readonly CoverageFrameArena _coverageArena = new();
    readonly Dictionary<(int, int), CoverageUploadEvidence> _coverageUploads = [];
    internal bool CoverageUploadRetainedForTests(MeshHandle mesh) =>
        _coverageUploads.ContainsKey((mesh.Index, mesh.Generation));

    static int AllocateCoverageSceneStamp()
    {
        int stamp = Interlocked.Increment(ref nextCoverageSceneStamp);
        if (stamp <= 0) throw new InvalidOperationException("The process exhausted scene ownership stamps.");
        return stamp;
    }

    void RequireCoverageThread()
    {
        if (Environment.CurrentManagedThreadId != _coverageThread)
            throw new CoverageFrameStateException(new(CoverageFailureCode.StaleFrame, null, 0, 0, 0, 0));
    }

    /// <summary>Admit every material slice of one exact uploaded annotated host and its immutable table.</summary>
    public SkinnedHostCoverage CreateHostCoverage(Skeleton skeleton, IReadOnlyList<SkinnedMeshHandle> meshes, CoverageRecessionTable table)
    {
        RequireCoverageThread();
        ObjectDisposedException.ThrowIf(_disposalComplete, this);
        ArgumentNullException.ThrowIfNull(skeleton); ArgumentNullException.ThrowIfNull(meshes); ArgumentNullException.ThrowIfNull(table);
        if (meshes.Count == 0) throw new ArgumentException("A host requires every material slice.", nameof(meshes));
        var uploaded = new SkinnedGltfMesh[meshes.Count];
        for (int i = 0; i < meshes.Count; i++)
        {
            var handle = meshes[i];
            if (handle.SceneStamp != _coverageSceneStamp || !_skinnedSlots.IsValid(handle.Index, handle.Generation)
                || _skinnedMeshes[handle.Index] is not { } entry)
                throw new HostCoverageDataException(HostCoverageRefusal.HostHandleOwnershipMismatch, "Coverage host handle is not owned by this live scene.")
                { HandleIndex = handle.Index, HandleGeneration = handle.Generation };
            uploaded[i] = entry.Source;
        }
        var first = uploaded[0].Coverage ?? throw new HostCoverageDataException(HostCoverageRefusal.UnannotatedMaterialSlice, "Coverage host needs annotated upload evidence.");
        GltfSkinnedMeshPart.ValidateCoverageParts(first, uploaded);
        if (!ReferenceEquals(first.SkeletonOwner, skeleton))
            throw first.Source.Error(HostCoverageRefusal.SkeletonMismatch, "Coverage requires its exact source skeleton owner.");
        if (uploaded[0].InverseBind.Length != skeleton.BoneCount || uploaded[0].RestPose.Length != skeleton.BoneCount)
            throw first.Source.Error(HostCoverageRefusal.IncompatibleSkin, "Coverage requires one bind and rest pose per skeleton bone.");
        var topology = CoverageRefinement.Build(first.Source, first.Map);
        var binding = new CoverageTableBinding(topology, table);
        var handles = topology.MaterialRanges.Select(range => meshes[Array.FindIndex(uploaded,
            m => m.Coverage!.MaterialSlice == range.MaterialSlice)]).ToArray();
        var owner = new SkinnedHostCoverage(this, handles, topology, uploaded[0].InverseBind.ToArray(), binding);
        try { ReserveCoverageStorage(owner); }
        catch { owner.Disposed = true; _coverageBatch?.RetireActor(owner); throw; }
        _coverageHosts.Add(owner);
        return owner;
    }

    internal void BindCoverageActor(SkinnedHostCoverage owner, MotionKey actor)
    {
        if (!owner.Actor.IsNone && owner.Actor != actor || _coverageHosts.Any(h => !ReferenceEquals(h, owner) && h.Actor == actor))
            throw new CoverageFrameStateException(new(CoverageFailureCode.IdentityMismatch, owner.Topology.HostIdentity, 0, 0, 0, 0));
        owner.Actor = actor;
    }
    internal void RetireCoverage(SkinnedHostCoverage owner)
    {
        _coverageBatch?.RetireActor(owner);
        _coverageHosts.Remove(owner);
    }
    internal bool IsCoveragePreparedThisFrame(SkinnedHostCoverage owner) =>
        _coverageArena.Generation != 0 && owner.Request.Generation == _coverageArena.Generation;
    bool CoverageHostsLive(SkinnedHostCoverage owner)
    {
        foreach (var handle in owner.Meshes)
            if (!_skinnedSlots.IsValid(handle.Index, handle.Generation)) return false;
        return true;
    }
    void DisposeCoverage()
    {
        AbandonCoverageSubmission();
        _coverageBatch?.Dispose(); _coverageBatch = null;
        foreach (var owner in _coverageHosts) { owner.Invalidate(); owner.Disposed = true; }
        _coverageHosts.Clear(); _coverageUploads.Clear(); _coverageArena.Begin();
    }

    internal bool StageCoverage(SkinnedHostCoverage owner, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        CoverageStateKey stateKey, ReadOnlySpan<CoverageCommandPose> commands, MotionKey actor,
        out SkinnedCoverageFrame frame, out CoverageFailure failure)
    {
        frame = default;
        var p = owner.Request;
        if (_disposalComplete || _coverageArena.Generation == 0 || _framePrepared || actor.IsNone || actor != owner.Actor
            || IsCoveragePreparedThisFrame(owner) || p.PublicationGeneration == _coverageArena.Generation)
            return Refuse(CoverageFailureCode.StaleFrame, out failure);
        p.AttemptGeneration = _coverageArena.Generation;
        p.Raw = false;
        if (commands.Length <= p.Commands.Length) { commands.CopyTo(p.Commands); p.CommandCount = commands.Length; }
        if (!CoverageHostsLive(owner)) return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
        if (!owner.Uniforms.TryPrepare(palette, owner.InverseBind, model, commands, out failure)) { p.Failure = failure; return false; }
        if (!owner.Binding.Table.TrySelect(stateKey, out int entry)) return Refuse(CoverageFailureCode.TableHole, out failure);
        if (commands.IsEmpty) return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
        for (int c = 0; c < commands.Length; c++)
        {
            var command = commands[c];
            CoverageEquipmentStage? found = null;
            foreach (var stage in owner.Equipment)
                if (stage.Generation == _coverageArena.Generation && stage.Revision == p.Revision && stage.Instance == command.EquipmentInstanceId
                    && stage.TryCommand(command.Id, out var support) && ReferenceEquals(support, command.Support)) found = stage;
            if (found is null || command.EquipmentWorld != found.Draws[0].World) return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
            for (int material = 0; material < found.Count; material++)
                if (!_slots.IsValid(found.Draws[material].Mesh.Index, found.Draws[material].Mesh.Generation))
                    return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
            p.Equipment[c] = found;
        }
        palette.CopyTo(p.Palette); commands.CopyTo(p.Commands);
        p.Model = model; p.StateKey = stateKey; p.EntryIndex = entry; p.CommandCount = commands.Length;
        p.Generation = _coverageArena.Generation;
        Array.Clear(p.Draws); Array.Clear(p.Outlines);
        frame = new(p, p.Generation, p.Revision);
        p.Failure = failure = default;
        return true;

        bool Refuse(CoverageFailureCode code, out CoverageFailure refused)
        { p.Failure = refused = new(code, owner.Topology.HostIdentity, 0, 0, 0, 0); return false; }
    }

    CoveragePreparation RequireCoverage(SkinnedCoverageFrame frame)
    {
        var p = frame.Preparation;
        if (p is null || _disposalComplete || !ReferenceEquals(p.Owner.Scene, this) || p.Owner.Disposed
            || frame.Generation != _coverageArena.Generation || frame.Generation != p.Generation || frame.Revision != p.Revision)
            throw StaleCoverage(p);
        p.Owner.RequireThread();
        if (!CoverageHostsLive(p.Owner)) throw StaleCoverage(p);
        for (int command = 0; command < p.CommandCount; command++)
        {
            var stage = p.Equipment[command]!;
            for (int material = 0; material < stage.Count; material++)
                if (!_slots.IsValid(stage.Draws[material].Mesh.Index, stage.Draws[material].Mesh.Generation)) throw StaleCoverage(p);
        }
        return p;
    }
    static CoverageFrameStateException StaleCoverage(CoveragePreparation? p) =>
        new(new(CoverageFailureCode.StaleFrame, p?.Owner.Topology.HostIdentity, 0, 0, 0, 0));

    CoveragePreparation? ValidateCoverageDraw(in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> palette, bool outline)
    {
        if (draw.Coverage.Preparation is null) return null;
        var p = RequireCoverage(draw.Coverage);
        int slice = CoverageSlice(p, draw.Mesh);
        if (_framePrepared || slice < 0 || draw.Model != p.Model || draw.Motion != p.Owner.Actor || !palette.SequenceEqual(p.Palette)
            || (outline ? p.Outlines[slice] : p.Draws[slice]) is not null) throw StaleCoverage(p);
        return p;
    }
    static void RecordCoverageDraw(CoveragePreparation? p, in SkinnedInstanceDraw draw, bool outline)
    {
        if (p is null) return;
        int slice = CoverageSlice(p, draw.Mesh);
        if (outline) p.Outlines[slice] = draw;
        else p.Draws[slice] = draw;
    }
    static int CoverageSlice(CoveragePreparation preparation, SkinnedMeshHandle handle)
    {
        var meshes = preparation.Owner.Meshes;
        for (int i = 0; i < meshes.Length; i++)
            if (meshes[i].Index == handle.Index && meshes[i].Generation == handle.Generation
                && meshes[i].SceneStamp == handle.SceneStamp) return i;
        return -1;
    }
    internal int CoverageEntryForTests(SkinnedCoverageFrame frame) => RequireCoverage(frame).EntryIndex;
    internal bool CoverageActorRegisteredForTests(MotionKey actor, IReadOnlyList<SkinnedMeshHandle> handles) =>
        _coverageHosts.Any(h => h.Actor == actor && h.Meshes.SequenceEqual(handles));
}
