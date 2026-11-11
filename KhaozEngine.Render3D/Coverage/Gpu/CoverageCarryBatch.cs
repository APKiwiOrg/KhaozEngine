using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>Bounded carry recording and submitted output-slot ownership. Scene-thread only.
/// The scene's aggregate participant owns registration, fence retention and the submission decision.</summary>
internal sealed class CoverageCarryBatch : IDisposable, IGpuFrameCompletionConsumer
{
    readonly IGpuDevice device;
    readonly GpuRetireQueue retired;
    readonly List<CoverageCarryActor> actors = [];
    readonly List<CoverageCarryGroup> groups = [];
    readonly IGpuResourceLayout[] layouts = new IGpuResourceLayout[CoverageCarryShaders.StageCount];
    readonly IGpuComputeShader[] shaders = new IGpuComputeShader[CoverageCarryShaders.StageCount];
    readonly IGpuComputePipeline[] pipelines = new IGpuComputePipeline[CoverageCarryShaders.StageCount];
    CoverageCarrySurface? storage;
    uint[] reset = [];
    int actorCapacity, sourceCapacity, vertexCapacity, triangleCapacity;
    int sources, vertices, triangles, workWords;
    bool pipelinesReady, unsupported, recorded, disposed;
    ulong submission;

    internal CoverageCarryBatch(IGpuDevice device, GpuRetireQueue retired)
    {
        ArgumentNullException.ThrowIfNull(device); ArgumentNullException.ThrowIfNull(retired);
        this.device = device; this.retired = retired;
        unsupported = !device.Capabilities.SupportsCompute || !device.Capabilities.SupportsCompletionFences;
    }
    internal int SlotsPerActor { get; private set; }
    internal bool DeviceRefused => unsupported;
    internal void RefuseDevice() { unsupported = true; AbandonRecording(); }

    internal void ReserveOwner(SkinnedHostCoverage owner)
    {
        if (SlotsPerActor == 0) Reserve(0, 0, 0, 0);
        if (unsupported || !EnsurePipelines()) return;
        PollCompleted();
        var topology = owner.Topology;
        if (ReusableActor(owner) < 0)
            Reserve(actors.Count + 1, checked(sources + topology.SourceVertexCount),
                checked(vertices + topology.VertexCount), checked(triangles + topology.TriangleCount));
        if (!TryRegister(owner, out _, out var failure)) throw new CoverageFrameStateException(failure);
    }

    int ReusableActor(SkinnedHostCoverage owner)
    {
        var topology = owner.Topology;
        for (int i = 0; i < actors.Count; i++)
        {
            var old = actors[i];
            var prior = old.Owner.Topology;
            if (!old.Retired || old.Pending >= 0 || topology.SourceVertexCount > prior.SourceVertexCount
                || topology.VertexCount > prior.VertexCount || topology.TriangleCount > prior.TriangleCount) continue;
            bool free = true;
            foreach (var slot in old.Slots) free &= slot.State == CoverageCarrySlotState.Free;
            if (free) return i;
        }
        return -1;
    }

    internal bool TryStageRaw(SkinnedHostCoverage owner, int outputSlot, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, out CoverageFailure failure)
    {
        ObjectDisposedException.ThrowIf(disposed, this); owner.RequireThread();
        var actor = Find(owner);
        if (unsupported) return Refuse(owner, CoverageFailureCode.UnsupportedAdapter, out failure);
        if (recorded || owner.Disposed || actor is null || actor.Pending != outputSlot || outputSlot < 0 || actor.Staged)
            return Refuse(owner, CoverageFailureCode.StaleFrame, out failure);
        if (!owner.Uniforms.TryPrepareRaw(palette, owner.InverseBind, model, out failure)) return false;
        CoverageCarryUpload.Frame(actor, -1, owner.Uniforms.ComposedPalette, []);
        actor.Staged = true; failure = default; return true;
    }

    internal void CancelOutput(SkinnedHostCoverage owner)
    {
        var actor = Find(owner);
        if (actor is null) return;
        if (actor.Pending >= 0) actor.Slots[actor.Pending] = default;
        actor.Pending = -1; actor.Staged = false;
    }

    internal void RetireActor(SkinnedHostCoverage owner)
    {
        var actor = Find(owner);
        if (actor is null) return;
        actor.Retired = true; actor.ReleaseHistory();
        // An encoded pending output still belongs to its recording, even after actor disposal.
        if (!recorded)
        {
            CancelOutput(owner);
            if (actor.Committed >= 0)
            {
                actor.Slots[actor.Committed].State = CoverageCarrySlotState.Retained;
                actor.Committed = -1;
            }
        }
    }

    internal void Discontinue(SkinnedHostCoverage owner, IGpuFence? completion)
    {
        var actor = Find(owner);
        if (actor is null || actor.Committed < 0) return;
        ref var slot = ref actor.Slots[actor.Committed];
        slot.State = CoverageCarrySlotState.Retained;
        slot.LastUse = submission; slot.Completion = completion; slot.Proof = submission;
        actor.Committed = -1;
        // Keep this generation lease reusable until migration or actor retirement.
    }

    /// <summary>Cold aggregate capacity, grown only outside a recording. Existing submitted ranges keep their
    /// buffer references, and migration becomes history only after successful submission.</summary>
    internal void Reserve(int actorCount, int sourceVertices, int finalVertices, int triangleCount)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(actorCount); ArgumentOutOfRangeException.ThrowIfNegative(sourceVertices);
        ArgumentOutOfRangeException.ThrowIfNegative(finalVertices); ArgumentOutOfRangeException.ThrowIfNegative(triangleCount);
        if (SlotsPerActor == 0) SlotsPerActor = checked(GpuRetireQueue.FramesInFlightFor(device) + 2);
        if (recorded || HasPendingOutput() || !GpuRecording.CanOpen(device)) throw new InvalidOperationException("Grow carry storage before acquiring recording outputs.");
        if (actorCount <= actorCapacity && sourceVertices <= sourceCapacity && finalVertices <= vertexCapacity && triangleCount <= triangleCapacity) return;
        int ac = Math.Max(actorCount, actorCapacity), sc = Math.Max(sourceVertices, sourceCapacity);
        int vc = Math.Max(finalVertices, vertexCapacity), tc = Math.Max(triangleCount, triangleCapacity);
        _ = CoverageCarryLayout.Bytes(checked((long)vc * SlotsPerActor * 16));
        _ = CoverageCarryLayout.WorkWords(ac, sc, vc, tc);
        var bindings = new CoverageCarryGroupResources[groups.Count];
        var leases = new CoverageCarryHistoryLease[actors.Count];
        var resets = new uint[checked(ac * 8)];
        var fresh = unsupported ? null : new CoverageCarrySurface(device, SlotsPerActor, ac, sc, vc, tc);
        try
        {
            for (int i = 0; i < groups.Count; i++) bindings[i] = groups[i].Allocate(ac, fresh!, layouts);
            for (int i = 0; i < actors.Count; i++)
            {
                device.UpdateBuffer(fresh!.Work, CoverageCarryLayout.Bytes(actors[i].WorkBase), actors[i].WorkHeader);
                leases[i] = new(fresh, retired);
            }
            actors.EnsureCapacity(ac); groups.EnsureCapacity(ac);
        }
        catch
        {
            foreach (var binding in bindings) binding?.Dispose();
            fresh?.Dispose(); throw;
        }
        for (int i = 0; i < groups.Count; i++) groups[i].Adopt(bindings[i], retired);
        for (int i = 0; i < actors.Count; i++) actors[i].FrameLease = leases[i];
        retired.Retire(storage); storage = fresh;
        actorCapacity = ac; sourceCapacity = sc; vertexCapacity = vc; triangleCapacity = tc;
        reset = resets;
        for (int i = 0; i < ac; i++) for (int axis = 0; axis < 3; axis++) reset[i * 8 + axis] = uint.MaxValue;
    }

    /// <summary>Lease a safe output. The caller publishes carry or ordinary current skin before submission.
    /// No free slot refuses, without waiting or changing submitted history.</summary>
    internal bool TryAcquireOutput(SkinnedHostCoverage owner, out int outputSlot, out CoverageFailure failure)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(owner); owner.RequireThread();
        outputSlot = -1;
        if (owner.Disposed || recorded) return Refuse(owner, CoverageFailureCode.StaleFrame, out failure);
        if (owner.Actor.IsNone) return Refuse(owner, CoverageFailureCode.IdentityMismatch, out failure);
        if (unsupported || !EnsurePipelines()) return Refuse(owner, CoverageFailureCode.UnsupportedAdapter, out failure);
        var actor = Find(owner);
        if (actor is null && !TryRegister(owner, out actor, out failure)) return false;
        if (actor!.Pending >= 0) return Refuse(owner, CoverageFailureCode.StaleFrame, out failure);
        for (int slot = 0; slot < SlotsPerActor; slot++)
        {
            ref var candidate = ref actor.Slots[slot];
            if (candidate.State != CoverageCarrySlotState.Free) continue;
            candidate = new() { State = CoverageCarrySlotState.Pending, Buffer = storage!.Surface, Base = actor.SlotBase(slot) };
            actor.Pending = outputSlot = slot;
            failure = default; return true;
        }
        return Refuse(owner, CoverageFailureCode.CapacityExceeded, out failure, SlotsPerActor + 1, SlotsPerActor);
    }

    /// <summary>Consume the accepted host preparation snapshot. Inputs are checked before writing packed uniforms.</summary>
    internal bool TryStage(SkinnedHostCoverage owner, int entryIndex, int outputSlot, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, ReadOnlySpan<CoverageCarryCommand> commands, out CoverageFailure failure)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(owner); owner.RequireThread();
        if (unsupported) return Refuse(owner, CoverageFailureCode.UnsupportedAdapter, out failure);
        var actor = Find(owner);
        if (commands.Length > CoverageCarryUniforms.MaxCommands)
            return Refuse(owner, CoverageFailureCode.CapacityExceeded, out failure, commands.Length, CoverageCarryUniforms.MaxCommands);
        if (recorded || owner.Disposed || actor is null || actor.Pending != outputSlot || outputSlot < 0
            || actor.Staged || !owner.IsPreparedThisFrame || owner.Request.EntryIndex != entryIndex
            || entryIndex < 0 || entryIndex >= owner.Binding.Table.EntryCount || owner.Request.Model != model
            || !palette.SequenceEqual(owner.Request.Palette) || !commands.SequenceEqual(owner.Uniforms.Commands))
            return Refuse(owner, CoverageFailureCode.StaleFrame, out failure);
        CoverageCarryUpload.Frame(actor, entryIndex, owner.Uniforms.ComposedPalette, commands);
        actor.Staged = true;
        failure = default; return true;
    }

    internal void Record(IGpuCommandList commands)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ArgumentNullException.ThrowIfNull(commands);
        if (recorded) throw new InvalidOperationException("Carry commands were already recorded.");
        bool active = false;
        foreach (var group in groups) group.Begin();
        foreach (var actor in actors)
        {
            if (actor.Pending >= 0) actor.CopyHistory(commands, storage!);
            if (!actor.Staged) continue;
            actor.Group.Add(actor); active = true;
        }
        if (!active) { recorded = HasPendingOutput(); return; }
        foreach (var group in groups)
            if (group.Active > 0) commands.UpdateBuffer<uint>(group.Frames, 0, group.FrameWords);
        commands.UpdateBuffer<uint>(storage!.Results, 0, reset.AsSpan(0, actors.Count * 8));
        foreach (var group in groups)
        {
            if (group.Active == 0) continue;
            for (int stage = 0; stage < CoverageCarryShaders.StageCount; stage++)
            {
                commands.SetComputePipeline(pipelines[stage]);
                commands.SetComputeResourceSet(0, group.Sets[stage]);
                commands.Dispatch(group.Groups(stage), 1, 1);
            }
        }
        recorded = true;
    }

    /// <summary>Previous binds submitted data, never a reconstructed row. A negative previous slot gives
    /// first-appearance previous equals current. During cold migration Record copies the old submitted range
    /// into the new storage before any consumer reads it, without changing the committed range.</summary>
    internal CoverageSurfaceRange Surface(SkinnedHostCoverage owner, int outputSlot, int previousSlot)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        owner.RequireThread();
        var actor = Find(owner) ?? throw new ArgumentException("Actor has no carry reservation.", nameof(owner));
        if ((uint)outputSlot >= (uint)SlotsPerActor || actor.Slots[outputSlot].State == CoverageCarrySlotState.Free
            || previousSlot >= SlotsPerActor || previousSlot >= 0 && actor.Slots[previousSlot].State == CoverageCarrySlotState.Free)
            throw new CoverageFrameStateException(new(CoverageFailureCode.StaleFrame, owner.Topology.HostIdentity, 0, 0, 0, 0));
        var current = actor.Slots[outputSlot];
        if (current.State == CoverageCarrySlotState.Pending && previousSlot >= 0 && previousSlot != actor.Committed)
            throw new CoverageFrameStateException(new(CoverageFailureCode.StaleFrame, owner.Topology.HostIdentity, 0, 0, 0, 0));
        var prior = previousSlot < 0 ? current : actor.Slots[previousSlot];
        if (current.State == CoverageCarrySlotState.Pending && previousSlot == actor.Committed && previousSlot >= 0
            && !ReferenceEquals(prior.Buffer, current.Buffer))
        { prior.Buffer = current.Buffer; prior.Base = actor.SlotBase(previousSlot); }
        return new(current.Buffer!, prior.Buffer!, actor.Group.Indices, current.Base, prior.Base,
            owner.Topology.VertexCount, owner.Topology.Indices.Length);
    }

    internal int CommittedSlot(SkinnedHostCoverage owner) => Find(owner)?.Committed ?? -1;

    /// <summary>Call only after successful submission. A missing diagnostic fence still commits logical
    /// history. A subsequent fence from this queue proves completion of earlier untracked uses.</summary>
    internal void CommitSubmitted(IGpuFence? completion)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ulong serial = ++submission;
        foreach (var actor in actors)
        {
            if (actor.Pending >= 0)
            {
                if (actor.Committed >= 0)
                {
                    ref var prior = ref actor.Slots[actor.Committed];
                    prior.State = CoverageCarrySlotState.Retained;
                    prior.LastUse = serial; prior.Completion = completion; prior.Proof = serial;
                }
                ref var output = ref actor.Slots[actor.Pending];
                output.State = CoverageCarrySlotState.Committed;
                output.LastUse = serial; output.Completion = completion; output.Proof = serial;
                actor.Committed = actor.Pending; actor.Pending = -1; actor.Staged = false;
                if (actor.Owner.Disposed) Discontinue(actor.Owner, completion);
                else actor.CommitHistory(storage!);
            }
            if (completion is null) continue;
            for (int i = 0; i < actor.Slots.Length; i++)
            {
                ref var slot = ref actor.Slots[i];
                if (slot.State is not (CoverageCarrySlotState.Committed or CoverageCarrySlotState.Retained) || slot.Completion is not null) continue;
                slot.Completion = completion; slot.Proof = serial;
            }
        }
        recorded = false;
    }

    /// <summary>A participant cannot distinguish an explicit abandon from a partially failed submission.
    /// Keep its candidate until the explicit-discard callback or a later queue proof makes reuse safe.</summary>
    internal void AbandonRecording(IGpuFence? completion)
    {
        ulong serial = HasPendingOutput() ? ++submission : submission;
        foreach (var actor in actors)
        {
            if (actor.Pending >= 0)
            {
                ref var slot = ref actor.Slots[actor.Pending];
                slot.State = CoverageCarrySlotState.Retained;
                slot.LastUse = slot.Proof = serial; slot.Completion = completion; slot.Quarantined = true;
            }
            actor.Pending = -1; actor.Staged = false;
            if (actor.Retired && actor.Committed >= 0)
            {
                actor.Slots[actor.Committed].State = CoverageCarrySlotState.Retained;
                actor.Committed = -1;
            }
        }
        recorded = false;
    }

    internal void AbandonRecording()
    {
        foreach (var actor in actors)
        {
            if (actor.Pending >= 0) actor.Slots[actor.Pending] = default;
            actor.Pending = -1; actor.Staged = false;
            if (actor.Retired && actor.Committed >= 0)
            {
                actor.Slots[actor.Committed].State = CoverageCarrySlotState.Retained;
                actor.Committed = -1;
            }
        }
        recorded = false;
    }

    internal void PollCompleted()
    {
        ulong proof = 0;
        foreach (var actor in actors)
            foreach (var slot in actor.Slots)
                if (slot.Completion is { } fence && fence.Signaled) proof = Math.Max(proof, slot.Proof);
        ReleaseThrough(proof);
    }
    public bool HasPending(IGpuFence completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (disposed && (storage is null || storage.IsDisposed)) return false;
        PollCompleted();
        foreach (var actor in actors)
            foreach (var slot in actor.Slots)
                if (ReferenceEquals(slot.Completion, completion)) return true;
        return false;
    }
    public void DiscardUnsubmitted(IGpuFence completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        AbandonRecording();
        foreach (var actor in actors)
            for (int i = 0; i < actor.Slots.Length; i++)
                if (actor.Slots[i].Quarantined && ReferenceEquals(actor.Slots[i].Completion, completion))
                    actor.Slots[i] = default;
    }
    public void ReleaseCompleted(IGpuFence completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ulong proof = 0;
        foreach (var actor in actors)
            foreach (var slot in actor.Slots)
                if (ReferenceEquals(slot.Completion, completion)) proof = Math.Max(proof, slot.Proof);
        ReleaseThrough(proof);
    }
    void ReleaseThrough(ulong proof)
    {
        if (proof == 0) return;
        foreach (var actor in actors)
            for (int i = 0; i < actor.Slots.Length; i++)
            {
                ref var slot = ref actor.Slots[i];
                if (slot.LastUse > proof || slot.State is not (CoverageCarrySlotState.Retained or CoverageCarrySlotState.Committed)) continue;
                if (slot.State == CoverageCarrySlotState.Retained) slot = default;
                else { slot.Completion = null; slot.Proof = 0; }
            }
    }

    CoverageCarryActor? Find(SkinnedHostCoverage owner)
    { foreach (var actor in actors) if (ReferenceEquals(actor.Owner, owner)) return actor; return null; }
    bool HasPendingOutput()
    { foreach (var actor in actors) if (actor.Pending >= 0) return true; return false; }

    bool TryRegister(SkinnedHostCoverage owner, out CoverageCarryActor? actor, out CoverageFailure failure)
    {
        actor = null;
        var t = owner.Topology;
        int reusable = ReusableActor(owner);
        if (reusable < 0 && actors.Count >= actorCapacity) return Refuse(owner, CoverageFailureCode.CapacityExceeded, out failure, actors.Count + 1, actorCapacity);
        if (reusable < 0 && (long)sources + t.SourceVertexCount > sourceCapacity) return Refuse(owner, CoverageFailureCode.CapacityExceeded, out failure, t.SourceVertexCount, sourceCapacity - sources);
        if (reusable < 0 && (long)vertices + t.VertexCount > vertexCapacity) return Refuse(owner, CoverageFailureCode.CapacityExceeded, out failure, t.VertexCount, vertexCapacity - vertices);
        if (reusable < 0 && (long)triangles + t.TriangleCount > triangleCapacity) return Refuse(owner, CoverageFailureCode.CapacityExceeded, out failure, t.TriangleCount, triangleCapacity - triangles);
        if (t.Map.Regions.Count > CoverageCarryLayout.MaxRegions) return Refuse(owner, CoverageFailureCode.CapacityExceeded, out failure, t.Map.Regions.Count, CoverageCarryLayout.MaxRegions);
        CoverageCarryGroup? group = null;
        foreach (var candidate in groups)
            if (ReferenceEquals(candidate.Table, owner.Binding.Table) && candidate.Bones == owner.InverseBind.Length) { group = candidate; break; }
        if (group is null)
        {
            group = new(device, owner);
            try { group.Reserve(actorCapacity, storage!, layouts, retired); }
            catch { group.Dispose(); throw; }
            groups.Add(group);
        }
        if (reusable >= 0)
        {
            var old = actors[reusable];
            actor = new(owner, group, SlotsPerActor, old.WorkBase, old.SurfaceBase, old.ResultBase, storage!, retired);
            device.UpdateBuffer(storage!.Work, CoverageCarryLayout.Bytes(actor.WorkBase), actor.WorkHeader);
            actors[reusable] = actor;
            if (!ReferenceEquals(old.Group, group) && !actors.Exists(a => ReferenceEquals(a.Group, old.Group)))
            { groups.Remove(old.Group); retired.Retire(old.Group); }
            failure = default; return true;
        }
        actor = new(owner, group, SlotsPerActor, (uint)workWords, checked((uint)vertices * (uint)SlotsPerActor * 16),
            (uint)actors.Count * 8, storage!, retired);
        device.UpdateBuffer(storage!.Work, CoverageCarryLayout.Bytes(actor.WorkBase), actor.WorkHeader);
        actors.Add(actor);
        sources += t.SourceVertexCount; vertices += t.VertexCount; triangles += t.TriangleCount;
        workWords = checked(workWords + CoverageCarryLayout.WorkWords(1, t.SourceVertexCount, t.VertexCount, t.TriangleCount));
        failure = default; return true;
    }
    bool EnsurePipelines()
    {
        if (pipelinesReady) return true;
        if (unsupported) return false;
        try
        {
            for (int stage = 0; stage < CoverageCarryShaders.StageCount; stage++)
            {
                layouts[stage] = device.Factory.CreateResourceLayout(CoverageCarryLayout.Resources(stage));
                shaders[stage] = device.Factory.CreateComputeShaderFromSpirv(CoverageCarryShaders.Kernels[stage].Source, CoverageCarryShaders.Options);
                pipelines[stage] = device.Factory.CreateComputePipeline(new(shaders[stage], layouts[stage]));
            }
            foreach (var shader in shaders) shader.EnsureCompilationReady();
            pipelinesReady = true; return true;
        }
        catch (Exception error) when (error is NotSupportedException or ShaderValidationException)
        {
            for (int i = 0; i < CoverageCarryShaders.StageCount; i++) { pipelines[i]?.Dispose(); shaders[i]?.Dispose(); layouts[i]?.Dispose(); }
            Array.Clear(pipelines); Array.Clear(shaders); Array.Clear(layouts);
            unsupported = true; return false;
        }
    }
    static bool Refuse(SkinnedHostCoverage owner, CoverageFailureCode code, out CoverageFailure failure, int count = 0, int limit = 0)
    { failure = new(code, owner.Topology.HostIdentity, 0, 0, count, limit); return false; }
    public void Dispose()
    {
        if (disposed) return;
        AbandonRecording(); disposed = true;
        foreach (var actor in actors)
        {
            actor.ReleaseHistory();
            for (int i = 0; i < actor.Slots.Length; i++)
                if (actor.Slots[i].State == CoverageCarrySlotState.Committed) actor.Slots[i].State = CoverageCarrySlotState.Retained;
        }
        foreach (var group in groups) retired.Retire(group);
        for (int i = 0; i < CoverageCarryShaders.StageCount; i++) { retired.Retire(pipelines[i]); retired.Retire(shaders[i]); retired.Retire(layouts[i]); }
        retired.Retire(storage);
    }
}
