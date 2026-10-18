using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Shared pure admission checks. Frame-specific arithmetic runs after each command passes this prefix.</summary>
internal static class CoverageFrameAdmission
{
    const double MaxCondition = 1e6;

    internal static bool TryBegin(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands, int commandCapacity,
        Span<int> order, Span<int> regionCommands, out CoverageFailure failure)
    {
        failure = default;
        if (commands.Length > commandCapacity)
            return CoverageFrames.Refuse(topology, CoverageFailureCode.CapacityExceeded, 0, 0, commands.Length, commandCapacity, out failure);
        order = order[..commands.Length];
        for (int i = 0; i < order.Length; i++)
        {
            int j = i;
            while (j > 0 && commands[order[j - 1]].Id > commands[i].Id) { order[j] = order[j - 1]; j--; }
            order[j] = i;
        }
        regionCommands.Fill(-1);
        return true;
    }

    internal static bool TryActor(in Matrix4x4 model, out CoverageAffine actor) =>
        CoverageAffine.TryFrom(model, out actor) && Valid(actor) && actor.TryInverse(out _);

    internal static bool TryCommand(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands, ReadOnlySpan<int> order,
        Span<int> regionCommands, int slot, bool actorValid, out CoverageAffine equipment, out CoverageAffine coverage, out CoverageFailure failure)
    {
        equipment = coverage = default;
        failure = default;
        CoverageCommandPose command = commands[order[slot]];
        if (command.Id == 0 || command.EquipmentInstanceId == 0 || !ReferenceEquals(command.Topology, topology) || command.Support is null || slot > 0 && commands[order[slot - 1]].Id == command.Id)
            return CoverageFrames.Refuse(topology, CoverageFailureCode.IdentityMismatch, command, out failure);
        if (command.RegionIndex < 0 || command.RegionIndex >= regionCommands.Length)
            return CoverageFrames.Refuse(topology, CoverageFailureCode.UnknownRegion, command, out failure);
        if (regionCommands[command.RegionIndex] >= 0)
            return CoverageFrames.Refuse(topology, CoverageFailureCode.OverlappingConformers, command, out failure);
        regionCommands[command.RegionIndex] = slot;
        CoverageSupportSurface support = command.Support;
        if (support.InwardTriangles.Count > CoverageSupportSurface.MaxTriangles)
            return CoverageFrames.Refuse(topology, CoverageFailureCode.CapacityExceeded, command.EquipmentInstanceId, command.Id,
            support.InwardTriangles.Count, CoverageSupportSurface.MaxTriangles, out failure);
        if (!float.IsFinite(command.MinZ) || !float.IsFinite(command.MaxZ) || !(command.MinZ < command.MaxZ)
            || !float.IsFinite(command.CollarWidth) || !(command.CollarWidth > 0) || !float.IsFinite(command.Clearance)
            || !(command.Clearance > 0) || !float.IsFinite(command.MaxWorldRecession) || !(command.MaxWorldRecession >= 0)
            || !support.InwardOfClosedSolid)
            return CoverageFrames.Refuse(topology, CoverageFailureCode.UnsupportedSupport, command, out failure);
        if (!actorValid || !float.IsFinite(command.TransitionWeight) || command.TransitionWeight is < 0 or > 1
            || !CoverageAffine.TryFrom(command.EquipmentWorld, out equipment) || !Valid(equipment)
            || !CoverageAffine.TryFrom(command.CoverageWorld, out coverage) || !Valid(coverage)
            || !CoverageAffine.TryFrom(support.CoverageLocal, out _))
            return CoverageFrames.Refuse(topology, CoverageFailureCode.UnsupportedTransform, command, out failure);
        if (command.CoverageWorld != support.CoverageLocal * command.EquipmentWorld)
            return CoverageFrames.Refuse(topology, CoverageFailureCode.StaleFrame, command, out failure);
        return true;
    }

    static bool Valid(in CoverageAffine m)
    {
        if (!(m.Determinant > 0) || !m.TryInverse(out var inverse)) return false;
        double condition = m.Frobenius * inverse.Frobenius;
        return double.IsFinite(condition) && condition <= MaxCondition;
    }

}
