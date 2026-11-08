using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Immutable pointwise faces produce scalar results. Position groups gather those results before any vertex moves.</summary>
internal static class CoverageReferenceCorrection
{
    internal static bool TryApplyPrepared(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch scratch, out CoverageFailure failure)
    {
        Validate(topology, commands, scratch);
        Array.Clear(scratch.FaceCorrections);
        var tally = new CoverageTally();
        for (int slot = 0; slot < scratch.ActiveCommands; slot++)
        {
            var frame = scratch.Frames[slot];
            var command = commands[frame.Command];
            float clearance = frame.ActualClearanceCoverage;
            float conversion = 0;
            for (int t = 0; t < topology.TriangleCount; t++)
            {
                float correction;
                var face = topology.Triangles[t];
                if (face.Role != CoverageTriangleRole.Core || face.RegionIndex != command.RegionIndex) continue;
                _ = TryFace(scratch.Arithmetic, scratch, t, slot, scratch.Supports[slot]!, frame,
                    clearance, conversion, ref tally, out correction);
                scratch.FaceCorrections[t] = correction;
            }
        }
        if (!tally.Succeeded(topology, commands, scratch.Order, out failure)) return false;
        for (int g = 0; g < topology.PositionGroupCount; g++)
        {
            int representative = scratch.ActiveGroupRepresentatives[g];
            if (representative < 0) continue;
            GatherGroup(scratch.Arithmetic, scratch, g, representative);
        }
        // Guard reduction is independent of the group evaluation order and command submission order.
        for (int v = 0; v < topology.VertexCount; v++)
        {
            var record = topology.Vertices[v];
            int g = record.PositionGroup;
            if (scratch.ActiveGroupRepresentatives[g] != v) continue;
            int slot = scratch.RegionCommands[record.RegionIndex];
            CheckFullTarget(scratch.Arithmetic, scratch.Before[v].Position, scratch.GroupPositions[g], scratch.FullRecessions[g], slot,
                commands, scratch, ref tally);
        }
        if (!tally.Succeeded(topology, commands, scratch.Order, out failure)) return false;
        for (int v = 0; v < topology.VertexCount; v++)
        {
            int g = topology.Vertices[v].PositionGroup;
            if (scratch.ActiveGroupRepresentatives[g] < 0) continue;
            scratch.After[v].Position = scratch.GroupPositions[g];
        }
        return true;
    }

    static void GatherGroup(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch, int g, int representative)
    {
        var gathers = scratch.ReferenceTopology;
        var record = gathers.Vertices[representative];
        float correction = 0;
        for (int i = scratch.Core.Offsets[g]; i < scratch.Core.Offsets[g + 1]; i++)
            correction = f.Max(correction, scratch.FaceCorrections[scratch.Core.Faces[i]]);
        float total = f.Equal(correction, 0) ? scratch.FullRecessions[g] : f.Add(scratch.FullRecessions[g], correction);
        scratch.FullRecessions[g] = total;
        if (f.NotEqual(correction, 0))
        {
            Vector3 before = scratch.Before[representative].Position;
            Vector3 d = scratch.Frames[scratch.RegionCommands[record.RegionIndex]].Recess;
            scratch.GroupPositions[g] = new Vector3(f.Sub(before.X, (f.Mul(total, d.X))), f.Sub(before.Y, (f.Mul(total, d.Y))), f.Sub(before.Z, (f.Mul(total, d.Z))));
        }
    }

    static bool TryFace(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch, int triangle, int slot,
        CoverageReferenceSupport support, in CoverageReferenceFrame frame, float clearance, float conversion,
        ref CoverageTally tally, out float correction)
    {
        float discrepancy;
        float maximum;
        correction = 0;
        Span<Vector3> host = stackalloc Vector3[3];
        if (!PrepareFace(f, scratch.ReferenceTopology, scratch, triangle, slot, support, frame, clearance, conversion,
            host, ref tally, out discrepancy)) return false;
        if (!support.TryOverlayMaximum(f, host, out maximum))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return false; }
        return FinishFace(f, scratch.ReferenceTopology, triangle, slot, frame, clearance, conversion, maximum, discrepancy,
            ref tally, out correction);
    }

    static bool PrepareFace(CoverageReferenceArithmetic f, CoverageReferenceTopology data, CoverageReferenceScratch scratch,
        int triangle, int slot, CoverageReferenceSupport support, in CoverageReferenceFrame frame, float clearance, float conversion,
        Span<Vector3> host, ref CoverageTally tally, out float discrepancy)
    {
        discrepancy = 0;
        if (!(f.Greater(frame.CorrectionRate, 0)) || !f.IsFinite(frame.CorrectionRate)
            || !f.IsFinite(clearance) || f.Less(clearance, 0) || !f.IsFinite(conversion) || f.Less(conversion, 0))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return false; }
        bool valid = true;
        for (int corner = 0; corner < 3; corner++)
        {
            float ignored1;
            float ignored2;
            int vertex = (int)data.Indices[triangle * 3 + corner];
            Vector3 position = scratch.After[vertex].Position;
            Vector3 actual = CoverageReferenceMath.Point(f, position, frame.HostToActualSheet),
                nominal = CoverageReferenceMath.Point(f, position, frame.HostToCoverage);
            if (!CoverageReferenceMath.IsFinite3(f, actual) || !CoverageReferenceMath.IsFinite3(f, nominal))
            { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); valid = false; continue; }
            if (!support.TryHeight(f, new Vector2(actual.X, actual.Y), out ignored1) || !support.TryHeight(f, new Vector2(nominal.X, nominal.Y), out ignored2))
            { tally.Note(CoverageFailureCode.CoreEscaped, slot); valid = false; continue; }
            host[corner] = nominal;
            float x = f.Sub(actual.X, nominal.X), y = f.Sub(actual.Y, nominal.Y), z = f.Sub(actual.Z, nominal.Z);
            float loss = f.Add(f.Abs(z), (f.Mul(support.MaxSlope, f.Sqrt(f.Add((f.Mul(x, x)), (f.Mul(y, y)))))));
            if (!f.IsFinite(loss))
            { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); valid = false; continue; }
            discrepancy = f.Max(discrepancy, loss);
        }
        if (!valid) return false;
        return true;
    }

    static bool FinishFace(CoverageReferenceArithmetic f, CoverageReferenceTopology data, int triangle, int slot,
        in CoverageReferenceFrame frame, float clearance, float conversion, float maximum, float discrepancy,
        ref CoverageTally tally, out float correction)
    {
        correction = 0;
        float deficit = f.Add((f.Add(maximum, clearance)), discrepancy);
        if (!f.IsFinite(deficit))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return false; }
        if (f.LessOrEqual(deficit, 0)) return true;
        correction = f.Div((f.Add(deficit, conversion)), frame.CorrectionRate);
        if (!f.IsFinite(correction))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); correction = 0; return false; }
        bool valid = true;
        // As in the exact path, only a correcting face needs every corner at full strength.
        for (int corner = 0; corner < 3; corner++)
            if (f.NotEqual(data.Vertices[(int)data.Indices[triangle * 3 + corner]].Strength, 1f))
            { tally.Note(CoverageFailureCode.FoldedPatch, slot); valid = false; }
        if (valid) return true;
        correction = 0;
        return false;
    }

    static void CheckFullTarget(CoverageReferenceArithmetic f, Vector3 before, Vector3 full, float total, int slot,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch scratch, ref CoverageTally tally)
    {
        var frame = scratch.Frames[slot];
        var command = commands[frame.Command];
        if (!f.IsFinite(total) || !CoverageReferenceMath.IsFinite3(f, full))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return; }
        Vector3 delta = new Vector3(f.Sub(full.X, before.X), f.Sub(full.Y, before.Y), f.Sub(full.Z, before.Z));
        Vector3 world = CoverageReferenceMath.Linear(f, delta, frame.ActorLinear);
        float distance = f.Sqrt(CoverageReferenceMath.Dot3(f, world, world));
        Vector3 q = CoverageReferenceMath.Point(f, full, frame.HostToCoverage);
        float lower = f.Add(command.MinZ, frame.CapMargin), upper = f.Sub(command.MaxZ, frame.CapMargin);
        if (!CoverageReferenceMath.IsFinite3(f, delta) || !CoverageReferenceMath.IsFinite3(f, world) || !f.IsFinite(distance)
            || !CoverageReferenceMath.IsFinite3(f, q) || !f.IsFinite(lower) || !f.IsFinite(upper))
        { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); return; }
        if (f.Greater(distance, command.MaxWorldRecession)) tally.Note(CoverageFailureCode.RecessionExceeded, slot);
        if (f.Less(q.Z, lower) || f.Greater(q.Z, upper)) tally.Note(CoverageFailureCode.CapContact, slot);
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
            TransitionGroup(scratch.Arithmetic, commands, scratch, g, representative);
        }
        for (int v = 0; v < topology.VertexCount; v++)
        {
            int g = topology.Vertices[v].PositionGroup, representative = scratch.ActiveGroupRepresentatives[g];
            if (representative < 0) continue;
            StoreTransition(scratch.Arithmetic, commands, scratch, v, g, representative);
        }
    }

    static void TransitionGroup(CoverageReferenceArithmetic f, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch scratch, int g, int representative)
    {
        var data = scratch.ReferenceTopology;
        var record = data.Vertices[representative];
        var frame = scratch.Frames[scratch.RegionCommands[record.RegionIndex]];

        float weight = commands[frame.Command].TransitionWeight;
        Vector3 before = scratch.Before[representative].Position;
        if (f.Equal(weight, 0)) scratch.GroupPositions[g] = before;
        else if (f.NotEqual(weight, 1))
        {
            float applied = f.Mul(weight, scratch.FullRecessions[g]);
            scratch.GroupPositions[g] = new Vector3(f.Sub(before.X, (f.Mul(applied, frame.Recess.X))), f.Sub(before.Y, (f.Mul(applied, frame.Recess.Y))),
                f.Sub(before.Z, (f.Mul(applied, frame.Recess.Z))));
        }
    }

    static void StoreTransition(CoverageReferenceArithmetic f, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch scratch, int v, int g, int representative)
    {
        var data = scratch.ReferenceTopology;
        var record = data.Vertices[representative];
        int slot = scratch.RegionCommands[record.RegionIndex];

        if (f.Equal(commands[scratch.Frames[slot].Command].TransitionWeight, 0)) scratch.After[v] = scratch.Before[v];
        else scratch.After[v].Position = scratch.GroupPositions[g];
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
