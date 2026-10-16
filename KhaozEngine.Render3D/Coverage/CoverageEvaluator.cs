using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Bounded directional conformation of one posed host surface, followed by every-pose certification and final
/// normals. Eligible points recede along coverage -Z toward the actual inward support sheet, or the height of its nearest
/// actual boundary point through the finite collar. Nothing is clamped, enlarged or approximated into acceptance.</summary>
public static class CoverageEvaluator
{
    /// <inheritdoc cref="TryEvaluate(CoverageRefinement, ReadOnlySpan{ModelVertex}, in Matrix4x4, ReadOnlySpan{CoverageCommandPose}, CoverageScratch, Span{ModelVertex}, out CoverageCertificate, out CoverageFailure)"/>
    public static bool TryEvaluate(CoverageRefinement topology, ReadOnlySpan<ModelVertex> posedSource, in Matrix4x4 actorModel,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch, Span<ModelVertex> finalVertices, out CoverageFailure failure) =>
        TryEvaluate(topology, posedSource, actorModel, commands, scratch, finalVertices, out _, out failure);

    /// <summary>Skins nothing itself. posedSource holds exactly SourceVertexCount already skinned source vertices in actor
    /// model space, and finalVertices exactly VertexCount outputs, disjoint from it. Wrong lengths, overlap or foreign
    /// scratch throw ArgumentException. A refusal writes nothing to finalVertices, so no partial or stale surface is
    /// produced by this call, and the caller must present the uncovered pose instead. Warm calls allocate nothing.</summary>
    public static bool TryEvaluate(CoverageRefinement topology, ReadOnlySpan<ModelVertex> posedSource, in Matrix4x4 actorModel,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch, Span<ModelVertex> finalVertices,
        out CoverageCertificate certificate, out CoverageFailure failure)
    {
        ArgumentNullException.ThrowIfNull(topology); ArgumentNullException.ThrowIfNull(scratch);
        if (posedSource.Length != topology.SourceVertexCount || finalVertices.Length != topology.VertexCount || posedSource.Overlaps(finalVertices))
            throw new ArgumentException("Coverage evaluation requires exact source and final lengths and disjoint buffers.");
        if (!ReferenceEquals(scratch.Topology, topology))
            throw new ArgumentException("Coverage scratch must be built for this topology instance.", nameof(scratch));
        certificate = default;
        double magnitude = CoverageCertification.Magnitude(posedSource, default);
        if (!double.IsFinite(magnitude))
            return CoverageFrames.Refuse(topology, CoverageFailureCode.UnsupportedTransform, 0, 0, 0, 0, out failure);
        if (!CoverageFrames.TryPrepare(topology, actorModel, commands, scratch, magnitude, out failure)
            || !topology.TryInterpolate(posedSource, scratch.Before, out failure)
            || !Construct(topology, commands, scratch, out failure)
            || !CoverageCertification.Certify(topology, scratch.Before, scratch.After, commands, scratch, out certificate, out failure)
            || !CoverageSurfaceNormals.Update(topology, scratch.Before, scratch.After, scratch, out failure))
        {
            certificate = default;
            return false;
        }
        scratch.After.CopyTo(finalVertices);
        return true;
    }

    static bool Construct(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch, out CoverageFailure failure)
    {
        var tally = new CoverageTally();
        int stamp = scratch.NextStamp();
        ModelVertex[] before = scratch.Before, after = scratch.After;
        for (int i = 0; i < before.Length; i++)
        {
            CoverageRefinedVertex record = topology.Vertices[i];
            after[i] = before[i];
            int slot = record.RegionIndex >= 0 && record.Strength > 0 ? scratch.RegionCommands[record.RegionIndex] : -1;
            if (slot < 0) continue;
            int group = record.PositionGroup;
            if (scratch.GroupStamps[group] != stamp)
            {
                scratch.GroupStamps[group] = stamp;
                scratch.GroupPositions[group] = Conform(topology, i, before[i].Position, record, slot, commands, scratch, ref tally);
            }
            // Welded members share one computed position, so every seam receives identical bits.
            after[i].Position = scratch.GroupPositions[group];
        }
        return tally.Succeeded(topology, commands, scratch, out failure);
    }

    static Vector3 Conform(CoverageRefinement topology, int vertex, Vector3 position, in CoverageRefinedVertex record, int slot,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch, ref CoverageTally tally)
    {
        ref readonly CoverageCommandFrame frame = ref scratch.Frames[slot];
        ref readonly CoverageCommandPose command = ref commands[frame.Command];
        CoverageSupportSurface support = command.Support;
        CoverageVector q = frame.HostToCoverage.Apply(position);
        bool core = topology.IsCoreVertex(vertex);
        if (q.Z < command.MinZ + frame.CapMargin || q.Z > command.MaxZ - frame.CapMargin)
        {
            tally.Note(CoverageFailureCode.CapContact, slot); return position;
        }
        var xy = CoverageAffinePoint.FromDouble(q.X, q.Y);
        double spatialMin = 1, spatialMax = 1;
        if (!support.TryGetHeightBounds(xy, out CoverageScalarBounds height))
        {
            if (core) { tally.Note(CoverageFailureCode.CoreEscaped, slot); return position; }
            if (!support.TryGetBoundaryBounds(xy, out CoverageBoundaryBounds boundary))
            {
                tally.Note(CoverageFailureCode.UnsupportedSupport, slot); return position;
            }
            if (boundary.Distance.Min >= command.CollarWidth) return position;
            // Smoothstep falls with distance, so the far distance bound gives the low weight.
            spatialMin = Smoothstep(1 - boundary.Distance.Max / command.CollarWidth);
            spatialMax = Smoothstep(1 - boundary.Distance.Min / command.CollarWidth);
            height = boundary.Height;
        }
        double strength = core ? 1 : record.Strength;
        double targetLow = height.Min - frame.ClearanceCoverage - frame.Slack, targetHigh = height.Max - frame.ClearanceCoverage + frame.Slack;
        double requestHigh = Math.Max(0, q.Z - targetLow), requestLow = Math.Max(0, q.Z - targetHigh);
        if (requestHigh == 0 || spatialMax == 0) return position;
        // The conservative choice recedes by the upper bound. Its spread is certified uncertainty, never a midpoint.
        double recessHigh = strength * spatialMax * requestHigh, recessLow = strength * spatialMin * requestLow;
        if ((recessHigh - recessLow) * frame.ZScale > command.Clearance * CoverageFrames.MarginFraction)
        {
            tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return position;
        }
        if (recessHigh * frame.ZScale > command.MaxWorldRecession)
        {
            tally.Note(CoverageFailureCode.RecessionExceeded, slot); return position;
        }
        double applied = command.TransitionWeight * recessHigh;
        if (applied == 0) return position;
        if (q.Z - applied < command.MinZ + frame.CapMargin)
        {
            tally.Note(CoverageFailureCode.CapContact, slot); return position;
        }
        var moved = new Vector3((float)(position.X - applied * frame.Recess.X), (float)(position.Y - applied * frame.Recess.Y),
            (float)(position.Z - applied * frame.Recess.Z));
        if (!CoverageRefinement.Finite(moved))
        {
            tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return position;
        }
        return moved == position ? position : moved;
    }

    static double Smoothstep(double t)
    {
        double u = Math.Clamp(t, 0, 1);
        return u * u * (3 - 2 * u);
    }
}

/// <summary>Records the first refusal in deterministic vertex or triangle order and counts its repeats for the same
/// command, so a failure carries a complete count without depending on command submission order.</summary>
internal struct CoverageTally
{
    CoverageFailureCode code;
    int slot, count;

    internal readonly bool Failed => code != CoverageFailureCode.None;

    internal void Note(CoverageFailureCode failure, int commandSlot)
    {
        if (code == CoverageFailureCode.None) { code = failure; slot = commandSlot; count = 1; }
        else if (code == failure && slot == commandSlot && count < int.MaxValue) count++;
    }

    internal readonly bool Succeeded(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch,
        out CoverageFailure failure)
    {
        if (code == CoverageFailureCode.None) { failure = default; return true; }
        if (slot < 0) return CoverageFrames.Refuse(topology, code, 0, 0, count, 0, out failure);
        CoverageCommandPose command = commands[scratch.Frames[slot].Command];
        return CoverageFrames.Refuse(topology, code, command.EquipmentInstanceId, command.Id, count, 0, out failure);
    }
}
