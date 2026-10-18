using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Immutable pointwise faces produce scalar results. Position groups gather those results before any vertex moves.</summary>
internal static class CoverageReferenceCorrection
{
    internal static bool TryApply(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch scratch, out CoverageFailure failure) =>
        TryApply(topology, commands, scratch, out failure, default, default);

    /// <summary>Capture terms are sorted-slot, round-once overrides for like-for-like tests only. Empty spans use the reserve.</summary>
    internal static bool TryApply(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch scratch, out CoverageFailure failure, ReadOnlySpan<double> correctionClearanceOverride,
        ReadOnlySpan<double> correctionConversionOverride, Action<CoverageReferenceCorrectionWork>? observerForTests = null,
        bool reverseGroupsForTests = false)
    {
        Validate(topology, commands, scratch);
        if (correctionClearanceOverride.IsEmpty != correctionConversionOverride.IsEmpty
            || !correctionClearanceOverride.IsEmpty && (correctionClearanceOverride.Length != scratch.ActiveCommands
                || correctionConversionOverride.Length != scratch.ActiveCommands))
            throw new ArgumentException("Reference correction overrides require both terms for every active slot.");
        Array.Clear(scratch.Corrections);
        Array.Clear(scratch.FaceCorrections);
        Array.Clear(scratch.FaceGuards);
        var tally = new CoverageTally();
        for (int slot = 0; slot < scratch.ActiveCommands; slot++)
        {
            var frame = scratch.Frames[slot];
            var command = commands[frame.Command];
            float clearance = correctionClearanceOverride.IsEmpty ? frame.ActualClearanceCoverage : (float)correctionClearanceOverride[slot];
            float conversion = correctionConversionOverride.IsEmpty ? 0 : (float)correctionConversionOverride[slot];
            for (int t = 0; t < topology.TriangleCount; t++)
            {
                var face = topology.Triangles[t];
                if (face.Role != CoverageTriangleRole.Core || face.RegionIndex != command.RegionIndex) continue;
                scratch.FaceGuards[t] = TryFace(topology, scratch, t, slot, command.Support.ReferenceData, frame,
                    clearance, conversion, ref tally, out float correction);
                scratch.FaceCorrections[t] = correction;
                observerForTests?.Invoke(new(CoverageReferenceCorrectionPhase.Faces, t, -1, -1));
            }
        }
        if (!tally.Succeeded(topology, commands, scratch.Order, out failure)) return false;
        var gathers = scratch.ReferenceTopology;
        for (int index = 0; index < topology.PositionGroupCount; index++)
        {
            int g = reverseGroupsForTests ? topology.PositionGroupCount - 1 - index : index;
            int representative = scratch.ActiveGroupRepresentatives[g];
            if (representative < 0) continue;
            float correction = 0;
            for (int i = gathers.CoreFaceOffsets[g]; i < gathers.CoreFaceOffsets[g + 1]; i++)
                correction = MathF.Max(correction, scratch.FaceCorrections[gathers.CoreFaces[i]]);
            scratch.Corrections[g] = correction;
            float total = correction == 0 ? scratch.FullRecessions[g] : scratch.FullRecessions[g] + correction;
            scratch.FullRecessions[g] = total;
            if (correction != 0)
            {
                var record = topology.Vertices[representative];
                Vector3 before = scratch.Before[representative].Position;
                Vector3 d = scratch.Frames[scratch.RegionCommands[record.RegionIndex]].Recess;
                scratch.GroupPositions[g] = new(before.X - (total * d.X), before.Y - (total * d.Y), before.Z - (total * d.Z));
            }
            observerForTests?.Invoke(new(CoverageReferenceCorrectionPhase.Groups, -1, g, -1));
        }
        // Guard reduction is independent of the group evaluation order and command submission order.
        for (int v = 0; v < topology.VertexCount; v++)
        {
            var record = topology.Vertices[v];
            int g = record.PositionGroup;
            if (scratch.ActiveGroupRepresentatives[g] != v) continue;
            int slot = scratch.RegionCommands[record.RegionIndex];
            CheckFullTarget(scratch.Before[v].Position, scratch.GroupPositions[g], scratch.FullRecessions[g], slot,
                commands, scratch, ref tally);
        }
        if (!tally.Succeeded(topology, commands, scratch.Order, out failure)) return false;
        for (int v = 0; v < topology.VertexCount; v++)
        {
            int g = topology.Vertices[v].PositionGroup;
            if (scratch.ActiveGroupRepresentatives[g] < 0) continue;
            scratch.After[v].Position = scratch.GroupPositions[g];
            observerForTests?.Invoke(new(CoverageReferenceCorrectionPhase.Positions, -1, g, v));
        }
        return true;
    }

    static bool TryFace(CoverageRefinement topology, CoverageReferenceScratch scratch, int triangle, int slot,
        CoverageReferenceSupport support, in CoverageReferenceFrame frame, float clearance, float conversion,
        ref CoverageTally tally, out float correction)
    {
        correction = 0;
        if (!(frame.CorrectionRate > 0) || !float.IsFinite(frame.CorrectionRate)
            || !float.IsFinite(clearance) || clearance < 0 || !float.IsFinite(conversion) || conversion < 0)
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return false; }
        Span<Vector3> host = stackalloc Vector3[3];
        float discrepancy = 0;
        bool valid = true;
        for (int corner = 0; corner < 3; corner++)
        {
            int vertex = (int)topology.Indices[triangle * 3 + corner];
            if (topology.Vertices[vertex].Strength != 1f)
            { tally.Note(CoverageFailureCode.FoldedPatch, slot); valid = false; continue; }
            Vector3 position = scratch.After[vertex].Position;
            Vector3 actual = CoverageReferenceMath.Point(position, frame.HostToActualSheet),
                nominal = CoverageReferenceMath.Point(position, frame.HostToCoverage);
            if (!CoverageRefinement.Finite(actual) || !CoverageRefinement.Finite(nominal))
            { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); valid = false; continue; }
            if (!support.TryHeight(new(actual.X, actual.Y), out _) || !support.TryHeight(new(nominal.X, nominal.Y), out _))
            { tally.Note(CoverageFailureCode.CoreEscaped, slot); valid = false; continue; }
            host[corner] = nominal;
            float x = actual.X - nominal.X, y = actual.Y - nominal.Y, z = actual.Z - nominal.Z;
            float loss = MathF.Abs(z) + (support.MaxSlope * MathF.Sqrt((x * x) + (y * y)));
            if (!float.IsFinite(loss))
            { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); valid = false; continue; }
            discrepancy = MathF.Max(discrepancy, loss);
        }
        if (!valid) return false;
        if (!support.TryOverlayMaximum(host, out float maximum))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return false; }
        float deficit = (maximum + clearance) + discrepancy;
        if (!float.IsFinite(deficit))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return false; }
        correction = deficit <= 0 ? 0 : (deficit + conversion) / frame.CorrectionRate;
        if (float.IsFinite(correction)) return true;
        tally.Note(CoverageFailureCode.UnsupportedTransform, slot);
        correction = 0;
        return false;
    }

    static void CheckFullTarget(Vector3 before, Vector3 full, float total, int slot,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch scratch, ref CoverageTally tally)
    {
        var frame = scratch.Frames[slot];
        var command = commands[frame.Command];
        if (!float.IsFinite(total) || !CoverageRefinement.Finite(full))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return; }
        Vector3 delta = new(full.X - before.X, full.Y - before.Y, full.Z - before.Z);
        Vector3 world = CoverageReferenceMath.Linear(delta, frame.ActorLinear);
        float distance = MathF.Sqrt(CoverageReferenceMath.Dot3(world, world));
        Vector3 q = CoverageReferenceMath.Point(full, frame.HostToCoverage);
        float lower = command.MinZ + frame.CapMargin, upper = command.MaxZ - frame.CapMargin;
        if (!CoverageRefinement.Finite(delta) || !CoverageRefinement.Finite(world) || !float.IsFinite(distance)
            || !CoverageRefinement.Finite(q) || !float.IsFinite(lower) || !float.IsFinite(upper))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return; }
        if (distance > command.MaxWorldRecession) tally.Note(CoverageFailureCode.RecessionExceeded, slot);
        if (q.Z < lower || q.Z > upper) tally.Note(CoverageFailureCode.CapContact, slot);
    }

    /// <summary>Blend the full scalar once. Pure copies preserve Before at zero and validated full bits at one.</summary>
    internal static void ApplyTransition(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch scratch)
    {
        Validate(topology, commands, scratch);
        for (int g = 0; g < topology.PositionGroupCount; g++)
        {
            int representative = scratch.ActiveGroupRepresentatives[g];
            if (representative < 0) continue;
            var record = topology.Vertices[representative];
            var frame = scratch.Frames[scratch.RegionCommands[record.RegionIndex]];
            float weight = commands[frame.Command].TransitionWeight;
            Vector3 before = scratch.Before[representative].Position;
            if (weight == 0) scratch.GroupPositions[g] = before;
            else if (weight != 1)
            {
                float applied = weight * scratch.FullRecessions[g];
                scratch.GroupPositions[g] = new(before.X - (applied * frame.Recess.X), before.Y - (applied * frame.Recess.Y),
                    before.Z - (applied * frame.Recess.Z));
            }
        }
        for (int v = 0; v < topology.VertexCount; v++)
        {
            int g = topology.Vertices[v].PositionGroup, representative = scratch.ActiveGroupRepresentatives[g];
            if (representative < 0) continue;
            var record = topology.Vertices[representative];
            int slot = scratch.RegionCommands[record.RegionIndex];
            if (commands[scratch.Frames[slot].Command].TransitionWeight == 0) scratch.After[v] = scratch.Before[v];
            else scratch.After[v].Position = scratch.GroupPositions[g];
        }
    }

    static void Validate(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch scratch)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(scratch);
        if (!ReferenceEquals(topology, scratch.ReferenceTopology.Topology))
            throw new ArgumentException("Reference scratch must be built for this topology instance.", nameof(scratch));
        if (commands.Length != scratch.ActiveCommands || commands.Length > scratch.CommandCapacity)
            throw new ArgumentException("Reference correction requires prepared active command frames.", nameof(commands));
    }
}

internal enum CoverageReferenceCorrectionPhase { Faces, Groups, Positions }

/// <summary>Test-only observation at each face-result, group-result or alias-position store. Faces never name a destination group.</summary>
internal readonly record struct CoverageReferenceCorrectionWork(CoverageReferenceCorrectionPhase Phase, int Face, int Group, int Vertex);
