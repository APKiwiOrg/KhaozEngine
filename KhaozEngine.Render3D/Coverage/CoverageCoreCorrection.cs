using System;
using System.Numerics;
using static KhaozEngine.Render3D.CoverageIntervals;

namespace KhaozEngine.Render3D;

/// <summary>One local full-weight face correction gathered by welded position group. The independent certificate
/// remains mandatory. Neither a sampled extremum nor a lower-strength vertex can rescue a core face.</summary>
internal static class CoverageCoreCorrection
{
    internal static bool TryApply(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before, Span<ModelVertex> fullCandidate,
        in Matrix4x4 actorModel, ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch, out CoverageFailure failure)
    {
        Array.Clear(scratch.Corrections);
        var tally = new CoverageTally();
        for (int slot = 0; slot < scratch.ActiveCommands; slot++)
        {
            ref readonly CoverageCommandFrame frame = ref scratch.Frames[slot];
            ref readonly CoverageCommandPose command = ref commands[frame.Command];
            if (!TryTerms(before, actorModel, command, frame, out double clearance, out double rate, out double conversion))
            {
                tally.Note(CoverageFailureCode.UnsupportedTransform, slot); continue;
            }
            for (int t = 0; t < topology.TriangleCount; t++)
            {
                CoverageRefinedTriangle record = topology.Triangles[t];
                if (record.Role != CoverageTriangleRole.Core || record.RegionIndex != command.RegionIndex) continue;
                if (!TryDeficit(topology, fullCandidate, t, command.Support, frame, clearance, scratch,
                    out double deficit, out CoverageFailureCode code))
                {
                    tally.Note(code, slot); continue;
                }
                if (deficit <= 0) continue;
                // In the actual cached sheet chart, a coverage recession improves signed gap by at least rate.
                double correction = Divide(Add(new(deficit, deficit), new(conversion, conversion)), new(rate, rate)).Max;
                if (!double.IsFinite(correction)) { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); continue; }
                for (int corner = 0; corner < 3; corner++)
                {
                    int vertex = (int)topology.Indices[t * 3 + corner];
                    if (topology.Vertices[vertex].Strength != 1) { tally.Note(CoverageFailureCode.FoldedPatch, slot); continue; }
                    int group = topology.Vertices[vertex].PositionGroup;
                    scratch.Corrections[group] = Math.Max(scratch.Corrections[group], correction);
                }
            }
        }
        if (!tally.Succeeded(topology, commands, scratch, out failure)) return false;
        Apply(topology, before, fullCandidate, commands, scratch, ref tally);
        return tally.Succeeded(topology, commands, scratch, out failure);
    }

    static bool TryDeficit(CoverageRefinement topology, ReadOnlySpan<ModelVertex> candidate, int triangle,
        CoverageSupportSurface support, in CoverageCommandFrame frame, double clearance, CoverageScratch scratch,
        out double deficit, out CoverageFailureCode failure)
    {
        deficit = double.NegativeInfinity;
        failure = CoverageFailureCode.UnsupportedTransform;
        double discrepancy = 0;
        Span<CoverageAffinePoint> host = scratch.OverlayHost, sheet = scratch.OverlaySupport;
        for (int corner = 0; corner < 3; corner++)
        {
            Vector3 position = candidate[(int)topology.Indices[triangle * 3 + corner]].Position;
            CoverageBoundedVector actual = frame.HostToActualSheet.Apply(position);
            if (!CoverageSheetSide.Contains(support, actual)) { failure = CoverageFailureCode.CoreEscaped; return false; }
            CoverageVector q = frame.HostToCoverage.Apply(position);
            if (!double.IsFinite(q.MaxAbs)) return false;
            host[corner] = CoverageAffinePoint.FromDouble(q.X, q.Y, q.Z);
            // The nominal overlay and actual displayed sheet need not share a frame. The affine vertex discrepancy
            // bounds the whole face, and the sheet's global Lipschitz slope bounds height loss at the displaced XY.
            if (!support.TryGetHeightBounds(host[corner], out _)) { failure = CoverageFailureCode.CoreEscaped; return false; }
            discrepancy = Math.Max(discrepancy, GapLoss(actual - CoverageBoundedVector.From(q), support.MaxSlope));
        }
        for (int index = 0; index < support.InwardTriangles.Count; index++)
        {
            CoverageSourceTriangle s = support.InwardTriangles[index];
            sheet[0] = support.ProjectedPositions[s.A]; sheet[1] = support.ProjectedPositions[s.B]; sheet[2] = support.ProjectedPositions[s.C];
            if (!CoverageFaceOverlay.TryMaximum(host, sheet, out double upper)) return false;
            deficit = Math.Max(deficit, upper);
        }
        if (!double.IsFinite(deficit) || !double.IsFinite(discrepancy)) return false;
        deficit = Add(Add(new(deficit, deficit), new(clearance, clearance)), new(discrepancy, discrepancy)).Max;
        return double.IsFinite(deficit);
    }

    static bool TryTerms(ReadOnlySpan<ModelVertex> before, in Matrix4x4 actorModel, in CoverageCommandPose command,
        in CoverageCommandFrame frame, out double clearance, out double rate, out double conversion)
    {
        clearance = rate = conversion = double.NaN;
        if (!CoverageAffine.TryFrom(command.EquipmentWorld, out var equipment) || !CoverageBoundedAffine.From(equipment).TryInverse(out var inverse)
            || !CoverageAffine.TryFrom(command.Support.SourceToCoverage, out var projection)
            || !CoverageAffine.TryFrom(actorModel, out var actor) || !CoverageBoundedAffine.From(actor).TryInverse(out var actorInverse)) return false;
        var worldToSheet = CoverageBoundedAffine.Compose(inverse, CoverageBoundedAffine.From(projection));
        double slope = command.Support.MaxSlope;
        CoverageScalarBounds factor = Sqrt(Add(One, Square(new(slope, slope))));
        clearance = Multiply(Multiply(new(command.Clearance, command.Clearance), factor), SpectralUpper(worldToSheet)).Max;
        CoverageBoundedVector direction = frame.HostToActualSheet.ApplyLinear(CoverageBoundedVector.From(frame.Recess));
        rate = Subtract(direction.Z, Multiply(new(slope, slope), Sqrt(Add(Square(direction.X), Square(direction.Y))))).Min;
        // Both the original pointwise float conversion and the corrected total's conversion are covered. Eight
        // float unit roundoffs also dominate double request addition, product and subtraction. Include subnormals.
        CoverageScalarBounds inverseNorm = Sqrt(Add(Add(Square(actorInverse.R1.Length), Square(actorInverse.R2.Length)), Square(actorInverse.R3.Length)));
        double magnitude = CoverageCertification.Magnitude(before, default);
        double radius = Add(new(magnitude, magnitude),
            Multiply(new(command.MaxWorldRecession, command.MaxWorldRecession), inverseNorm)).Max;
        double error = Add(Multiply(new(radius, radius), new(1.0 / (1 << 21), 1.0 / (1 << 21))), new(Math.ScaleB(1, -147), Math.ScaleB(1, -147))).Max;
        var roundoff = new CoverageScalarBounds(-error, error);
        conversion = GapLoss(frame.HostToActualSheet.ApplyLinear(new(roundoff, roundoff, roundoff)), slope);
        return double.IsFinite(clearance) && double.IsFinite(rate) && rate > 0 && double.IsFinite(conversion);
    }

    static CoverageScalarBounds SpectralUpper(CoverageBoundedAffine map)
    {
        CoverageScalarBounds a = CoverageBoundedVector.Dot(map.R1, map.R1), b = CoverageBoundedVector.Dot(map.R2, map.R2),
            c = CoverageBoundedVector.Dot(map.R3, map.R3);
        double ab = Abs(CoverageBoundedVector.Dot(map.R1, map.R2)), ac = Abs(CoverageBoundedVector.Dot(map.R1, map.R3)),
            bc = Abs(CoverageBoundedVector.Dot(map.R2, map.R3));
        double upper = Math.Max(Add(Add(a, new(ab, ab)), new(ac, ac)).Max,
            Math.Max(Add(Add(b, new(ab, ab)), new(bc, bc)).Max, Add(Add(c, new(ac, ac)), new(bc, bc)).Max));
        return Sqrt(new(0, upper));
    }

    static double Abs(CoverageScalarBounds bounds) => Math.Max(Math.Abs(bounds.Min), Math.Abs(bounds.Max));
    static double GapLoss(CoverageBoundedVector error, double slope) =>
        Add(new(Abs(error.Z), Abs(error.Z)), Multiply(new(slope, slope), Sqrt(Add(Square(error.X), Square(error.Y))))).Max;

    static void Apply(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before, Span<ModelVertex> candidate,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch, ref CoverageTally tally)
    {
        int stamp = scratch.NextStamp();
        for (int i = 0; i < candidate.Length; i++)
        {
            CoverageRefinedVertex record = topology.Vertices[i];
            int slot = record.RegionIndex >= 0 && record.Strength > 0 ? scratch.RegionCommands[record.RegionIndex] : -1;
            if (slot < 0) continue;
            int group = record.PositionGroup;
            if (scratch.GroupStamps[group] != stamp)
            {
                scratch.GroupStamps[group] = stamp;
                ref readonly CoverageCommandFrame frame = ref scratch.Frames[slot];
                ref readonly CoverageCommandPose command = ref commands[frame.Command];
                Vector3 b = before[i].Position, a = candidate[i].Position;
                double correction = scratch.Corrections[group];
                if (correction > 0)
                {
                    double total = Add(new(scratch.FullRecessions[group], scratch.FullRecessions[group]), new(correction, correction)).Max;
                    scratch.FullRecessions[group] = total;
                    a = new((float)(b.X - total * frame.Recess.X), (float)(b.Y - total * frame.Recess.Y), (float)(b.Z - total * frame.Recess.Z));
                }
                scratch.GroupPositions[group] = a;
                if (!CoverageRefinement.Finite(a)) { tally.Note(CoverageFailureCode.UnsupportedTransform, slot); continue; }
                // Check the full target even for a partial or zero transition. Equality still uses the exact fallback.
                var delta = CoverageBoundedVector.From(a) - CoverageBoundedVector.From(b);
                var distance = a == b ? Zero : frame.BoundedActor.ApplyLinear(delta).Length;
                if ((!distance.IsFinite || distance.Max > command.MaxWorldRecession)
                    && !CoverageGeometry.SquaredDistanceWithinBudget(CoverageAffinePoint.Project(a, frame.ActorModel),
                        CoverageAffinePoint.Project(b, frame.ActorModel), command.MaxWorldRecession)) tally.Note(CoverageFailureCode.RecessionExceeded, slot);
                CoverageScalarBounds z = frame.BoundedHostToCoverage.Apply(a).Z;
                if (!z.IsFinite || z.Min < Add(new(command.MinZ, command.MinZ), new(frame.CapMarginUpper, frame.CapMarginUpper)).Max
                    || z.Max > Subtract(new(command.MaxZ, command.MaxZ), new(frame.CapMarginUpper, frame.CapMarginUpper)).Min)
                    tally.Note(CoverageFailureCode.CapContact, slot);
            }
            candidate[i].Position = scratch.GroupPositions[group];
        }
    }
}
