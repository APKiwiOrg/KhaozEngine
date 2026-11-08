using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

internal enum CoverageCarrySlotState { Free, Pending, Committed, Retained }

internal struct CoverageCarrySlot
{
    internal CoverageCarrySlotState State;
    internal IGpuBuffer? Buffer;
    internal uint Base;
    internal IGpuFence? Completion;
    internal ulong LastUse, Proof;
    internal bool Quarantined;
}

/// <summary>Actor-local slot state. Only the batch's submitted callback changes committed selection.</summary>
internal sealed class CoverageCarryActor
{
    internal readonly SkinnedHostCoverage Owner;
    internal readonly CoverageCarryGroup Group;
    internal readonly CoverageCarrySlot[] Slots;
    internal readonly uint[] Frame;
    internal readonly uint[] WorkHeader;
    internal readonly uint WorkBase, SurfaceBase, ResultBase;
    internal int Pending = -1, Committed = -1;
    internal bool Staged, Retired;
    internal CoverageCarryHistoryLease FrameLease;
    CoverageCarryHistoryLease? historyLease;
    CoverageCarrySurface? historyStorage;

    internal CoverageCarryActor(SkinnedHostCoverage owner, CoverageCarryGroup group, int slots,
        uint workBase, uint surfaceBase, uint resultBase, CoverageCarrySurface storage, GpuRetireQueue retired)
    {
        Owner = owner; Group = group; Slots = new CoverageCarrySlot[slots];
        Frame = new uint[CoverageCarryLayout.FrameWords(owner.InverseBind.Length)];
        WorkHeader = CoverageCarryLayout.WorkHeader(owner.Topology);
        WorkBase = workBase; SurfaceBase = surfaceBase; ResultBase = resultBase;
        FrameLease = new(storage, retired);
    }
    internal uint SlotBase(int slot) => checked(SurfaceBase + (uint)slot * (uint)Owner.Topology.VertexCount * 16);
    internal void CopyHistory(IGpuCommandList commands, CoverageCarrySurface storage)
    {
        if (Committed < 0) return;
        ref var prior = ref Slots[Committed];
        if (ReferenceEquals(prior.Buffer, storage.Surface)) return;
        commands.CopyBuffer(prior.Buffer!, checked((uint)prior.Base * 4u), storage.Surface,
            checked((uint)SlotBase(Committed) * 4u), CoverageCarryLayout.Bytes((long)Owner.Topology.VertexCount * 16));
    }
    internal void CommitHistory(CoverageCarrySurface storage)
    {
        if (ReferenceEquals(historyStorage, storage)) return;
        FrameLease.Activate(); historyLease?.Retire();
        historyLease = FrameLease; historyStorage = storage;
    }
    internal void ReleaseHistory() { historyLease?.Retire(); historyLease = null; historyStorage = null; }
}
