using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Per-command double frames and certified error terms, derived once per evaluation into actor scratch.</summary>
internal struct CoverageCommandFrame
{
    internal int Command;
    internal Matrix4x4 ActorModel;
    internal Vector3 CoverageZ;
    internal CoverageAffine HostToCoverage, ActorLinear, EquipmentRelative;
    /// <summary>Model-space displacement of one coverage unit along +Z. Recession subtracts a multiple of it.</summary>
    internal CoverageVector Recess;
    internal CoverageBoundedAffine BoundedHostToCoverage, BoundedActor, HostToActualSheet;
    internal double CapMarginUpper;
    internal double ZScale, SigmaMin, ClearanceCoverage, Slack, Uncertainty, ConstructionErrorBound, CapMargin, Clearance;
    internal bool Full;
}

/// <summary>Validates commands, identities and transforms before any work, and derives their frames.</summary>
internal static class CoverageFrames
{
    /// <summary>Share of the authored clearance that numerical uncertainty may consume before a frame refuses.</summary>
    internal const double MarginFraction = .25;

    internal static bool TryPrepare(CoverageRefinement topology, in Matrix4x4 actorModel, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageScratch scratch, double hostMagnitude, out CoverageFailure failure)
    {
        if (!CoverageCarryAdmission.TryBegin(topology, commands, scratch.CommandCapacity, scratch.Order, scratch.RegionCommands, out failure)) return false;
        Span<int> order = scratch.Order.AsSpan(0, commands.Length);
        scratch.ActiveCommands = commands.Length;
        if (commands.Length == 0) return true;
        bool actorValid = CoverageCarryAdmission.TryActor(actorModel, out var actor);
        for (int slot = 0; slot < order.Length; slot++)
        {
            CoverageCommandPose command = commands[order[slot]];
            if (!CoverageCarryAdmission.TryCommand(topology, commands, order, scratch.RegionCommands, slot, actorValid,
                out var equipment, out var coverage, out failure)) return false;
            if (!Derive(actor, equipment, coverage, command, hostMagnitude, out scratch.Frames[slot]))
                return Refuse(topology, CoverageFailureCode.UnsupportedTransform, command, out failure);
            scratch.Frames[slot].Command = order[slot];
            scratch.Frames[slot].ActorModel = actorModel;
            scratch.Frames[slot].CoverageZ = new(command.CoverageWorld.M31, command.CoverageWorld.M32, command.CoverageWorld.M33);
        }
        return true;
    }

    static bool Derive(in CoverageAffine actor, in CoverageAffine equipment, in CoverageAffine coverage, in CoverageCommandPose command,
        double hostMagnitude, out CoverageCommandFrame frame)
    {
        frame = default;
        if (!actor.TryInverse(out var actorInverse) || !coverage.TryInverse(out var coverageInverse)) return false;
        CoverageSupportSurface support = command.Support;
        var boundedCoverage = CoverageBoundedAffine.From(coverage);
        var boundedEquipment = CoverageBoundedAffine.From(equipment);
        if (!boundedCoverage.TryInverse(out var boundedCoverageInverse) || !boundedEquipment.TryInverse(out var boundedEquipmentInverse)
            || !CoverageAffine.TryFrom(support.SourceToCoverage, out var compiledProjection)
            || !CoverageBoundedAffine.From(compiledProjection).TryInverse(out _)) return false;
        frame.BoundedActor = CoverageBoundedAffine.From(actor);
        frame.BoundedHostToCoverage = CoverageBoundedAffine.Compose(frame.BoundedActor, boundedCoverageInverse);
        frame.HostToActualSheet = CoverageBoundedAffine.Compose(CoverageBoundedAffine.Compose(frame.BoundedActor, boundedEquipmentInverse),
            CoverageBoundedAffine.From(compiledProjection));
        if (!frame.BoundedHostToCoverage.IsFinite || !frame.HostToActualSheet.IsFinite) return false;
        var capNormal = new CoverageBoundedVector(boundedCoverageInverse.R1.Z, boundedCoverageInverse.R2.Z, boundedCoverageInverse.R3.Z);
        frame.CapMarginUpper = CoverageIntervals.Multiply(new(command.Clearance, command.Clearance), capNormal.Length).Max;
        frame.HostToCoverage = CoverageAffine.Multiply(actor, coverageInverse);
        frame.ActorLinear = actor.WithTranslation(default);
        frame.EquipmentRelative = equipment.WithTranslation(equipment.Translation - actor.Translation);
        frame.Recess = actorInverse.ApplyLinear(coverage.Row(2));
        frame.ZScale = coverage.Row(2).Length;
        frame.SigmaMin = 1 / coverageInverse.SpectralBound;
        frame.CapMargin = command.Clearance * coverageInverse.ColumnLength(2);
        frame.Clearance = command.Clearance;
        frame.Full = command.TransitionWeight == 1;
        double slope = support.MaxSlope;
        // The displayed sheet in this frame is source * EquipmentWorld * inverse(CoverageWorld). Its distance from the
        // exact compiled projection bounds the inverse and composition error of the supplied float frames.
        frame.Uncertainty = CoverageAffine.Deviation(CoverageAffine.Multiply(equipment, coverageInverse), support.SourceToCoverage,
            support.MaxSourceMagnitude) * (1 + 1e-9) + 1e-12 * (support.MaxSourceMagnitude + 1e-30);
        double recessionModel = command.MaxWorldRecession * actorInverse.Frobenius;
        // Final positions round once to float in model space. Carry that through to coverage Z, with double headroom.
        double rounding = (hostMagnitude + recessionModel) * (1.0 / (1 << 22)) * Math.Sqrt(3) * frame.HostToCoverage.Frobenius;
        // Max-norm coordinate error has XY Euclidean length at most sqrt(2) times that error.
        frame.Slack = (frame.Uncertainty + rounding) * (1 + Math.Sqrt(2) * slope) * (1 + 1e-9);
        frame.ClearanceCoverage = command.Clearance * Math.Sqrt(1 + slope * slope) / frame.SigmaMin * (1 + 1e-12);
        frame.ConstructionErrorBound = CoverageIntervals.Multiply(new(frame.Slack, frame.Slack), new(frame.ZScale, frame.ZScale)).Max;
        double consumed = frame.ConstructionErrorBound;
        return double.IsFinite(consumed) && double.IsFinite(frame.ClearanceCoverage) && consumed <= command.Clearance * MarginFraction;
    }

    internal static bool Refuse(CoverageRefinement topology, CoverageFailureCode code, in CoverageCommandPose command, out CoverageFailure failure) =>
        Refuse(topology, code, command.EquipmentInstanceId, command.Id, 0, 0, out failure);

    internal static bool Refuse(CoverageRefinement topology, CoverageFailureCode code, ulong equipment, uint id, int count, int limit,
        out CoverageFailure failure)
    {
        failure = new CoverageFailure(code, topology.HostIdentity, equipment, id, count, limit);
        return false;
    }
}
