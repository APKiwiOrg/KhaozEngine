using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render3D;

/// <summary>Every-pose certification of actual final float host positions against the actual displayed equipment.
/// It never reads a GPU result, an animation endpoint or a whitelist. Ambiguous predicates refuse.</summary>
public static class CoverageCertification
{
    const byte Changed = 1, Commanded = 2;

    public static bool Validate(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before, ReadOnlySpan<ModelVertex> after,
        in Matrix4x4 actorModel, ReadOnlySpan<CoverageCommandPose> commands, in CoverageRenderContext context,
        CoverageScratch scratch, out CoverageFailure failure) =>
        Validate(topology, before, after, actorModel, commands, context, scratch, out _, out failure);

    public static bool Validate(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before, ReadOnlySpan<ModelVertex> after,
        in Matrix4x4 actorModel, ReadOnlySpan<CoverageCommandPose> commands, in CoverageRenderContext context,
        CoverageScratch scratch, out CoverageCertificate certificate, out CoverageFailure failure)
    {
        bool accepted = ValidateCandidate(topology, before, after, actorModel, commands, context, true, scratch,
            out var geometry, out double renderError, out failure);
        certificate = accepted ? new(geometry.FullCoreClearance, geometry.MinimumCoreClearance,
            renderError, geometry.DisplacedVertices, context.Stamp) : default;
        return accepted;
    }

    /// <inheritdoc cref="ValidateGeometry(CoverageRefinement, ReadOnlySpan{ModelVertex}, ReadOnlySpan{ModelVertex}, in Matrix4x4, ReadOnlySpan{CoverageCommandPose}, CoverageScratch, out CoverageGeometryCertificate, out CoverageFailure)"/>
    public static bool ValidateGeometry(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before, ReadOnlySpan<ModelVertex> after,
        in Matrix4x4 actorModel, ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch, out CoverageFailure failure) =>
        ValidateGeometry(topology, before, after, actorModel, commands, scratch, out _, out failure);

    /// <summary>before is the interpolated posed surface and after the candidate final surface, both exactly VertexCount
    /// in actor model space. Checks, in order: finite positions, exact protected bytes and positional welds, caps, world
    /// recession budgets and core support domain, then nondegenerate injective projected patches and unfolded changed
    /// faces, new nonadjacent host intersections through the refitted host hierarchy, then for each full-weight command
    /// the certified world clearance and solid containment of every commanded core and collar triangle and the analytic
    /// whole-core side behind the inward sheet. Other commanded regions must stay separated from that equipment too.</summary>
    public static bool ValidateGeometry(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before, ReadOnlySpan<ModelVertex> after,
        in Matrix4x4 actorModel, ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch,
        out CoverageGeometryCertificate certificate, out CoverageFailure failure) =>
        ValidateCandidate(topology, before, after, actorModel, commands, default, false, scratch, out certificate, out _, out failure);

    static bool ValidateCandidate(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before, ReadOnlySpan<ModelVertex> after,
        in Matrix4x4 actorModel, ReadOnlySpan<CoverageCommandPose> commands, in CoverageRenderContext context, bool render,
        CoverageScratch scratch, out CoverageGeometryCertificate certificate, out double renderError, out CoverageFailure failure)
    {
        renderError = 0;
        ArgumentNullException.ThrowIfNull(topology); ArgumentNullException.ThrowIfNull(scratch);
        if (before.Length != topology.VertexCount || after.Length != topology.VertexCount)
            throw new ArgumentException("Coverage certification requires exact topology lengths.");
        if (!ReferenceEquals(scratch.Topology, topology))
            throw new ArgumentException("Coverage scratch must be built for this topology instance.", nameof(scratch));
        certificate = default;
        double magnitude = Magnitude(before, after);
        if (!double.IsFinite(magnitude))
            return CoverageFrames.Refuse(topology, CoverageFailureCode.UnsupportedTransform, 0, 0, 0, 0, out failure);
        // Late validation is bracketed whole by its caller, so it observes no inner pass.
        return CoverageFrames.TryPrepare(topology, actorModel, commands, scratch, magnitude, out failure)
            && CertifyCandidate(topology, before, after, actorModel, commands, context, render, scratch, null, out certificate,
                out renderError, out failure);
    }

    /// <summary>One full Certify of the exact final surface after TryPrepare. Render-aware, the captured render bound
    /// follows, then only the separation that bound can change. The observer brackets each certification pass as
    /// GeometryCertification. Any refusal clears the certificate.</summary>
    internal static bool CertifyCandidate(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before, ReadOnlySpan<ModelVertex> after,
        in Matrix4x4 actorModel, ReadOnlySpan<CoverageCommandPose> commands, in CoverageRenderContext context, bool render,
        CoverageScratch scratch, Action<CoverageStageSpan, bool>? observer, out CoverageGeometryCertificate certificate,
        out double renderError, out CoverageFailure failure)
    {
        renderError = 0;
        observer?.Invoke(CoverageStageSpan.GeometryCertification, true);
        bool certified = Certify(topology, before, after, commands, scratch, out certificate, out failure);
        observer?.Invoke(CoverageStageSpan.GeometryCertification, false);
        if (!certified || !render) return certified;
        if (!CoverageRenderError.TryBound(topology, after, actorModel, commands, scratch, context, out renderError, out failure))
        {
            certificate = default;
            return false;
        }
        // One command has no other commanded region, so the raised threshold has nothing to recheck.
        if (scratch.ActiveCommands <= 1) return true;
        observer?.Invoke(CoverageStageSpan.GeometryCertification, true);
        bool separated = RenderSeparation(topology, after, commands, scratch, out failure);
        observer?.Invoke(CoverageStageSpan.GeometryCertification, false);
        if (!separated) certificate = default;
        return separated;
    }

    internal static double Magnitude(ReadOnlySpan<ModelVertex> first, ReadOnlySpan<ModelVertex> second)
    {
        double magnitude = 0;
        foreach (ref readonly ModelVertex v in first) magnitude = Math.Max(magnitude, Math.Max(Math.Abs(v.Position.X), Math.Max(Math.Abs(v.Position.Y), Math.Abs(v.Position.Z))));
        foreach (ref readonly ModelVertex v in second) magnitude = Math.Max(magnitude, Math.Max(Math.Abs(v.Position.X), Math.Max(Math.Abs(v.Position.Y), Math.Abs(v.Position.Z))));
        return magnitude;
    }

    internal static bool Certify(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before, ReadOnlySpan<ModelVertex> after,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch, out CoverageGeometryCertificate certificate, out CoverageFailure failure)
    {
        certificate = default;
        scratch.CertificationWitnessForTests?.Invoke(CoverageCertificationWork.Full, -1);
        var tally = new CoverageTally();
        Span<int> stack = stackalloc int[64];
        Vertices(topology, before, after, commands, scratch, ref tally, out int displaced);
        if (tally.Failed) return tally.Succeeded(topology, commands, scratch, out failure);
        Span<int> signs = stackalloc int[CoverageScratch.MaxCommands];
        Faces(topology, before, after, scratch, signs, ref tally);
        if (!tally.Failed) Injectivity(topology, after, scratch, signs, stack, ref tally);
        if (!tally.Failed) HostIntersections(topology, after, scratch, stack, ref tally);
        bool full = true;
        double minimumCore = double.PositiveInfinity, errorBound = 0;
        if (!tally.Failed)
        {
            Components(topology, scratch);
            for (int slot = 0; slot < scratch.ActiveCommands; slot++)
            {
                ref readonly CoverageCommandFrame frame = ref scratch.Frames[slot];
                errorBound = Math.Max(errorBound, frame.ConstructionErrorBound);
                if (!frame.Full) { full = false; continue; }
                Equipment(topology, after, commands, scratch, slot, false, stack, ref minimumCore, ref tally);
                if (!tally.Failed) Containment(topology, after, commands, scratch, slot, ref tally);
                if (!tally.Failed) CoreSide(topology, after, commands, scratch, slot, ref tally);
                if (tally.Failed) break;
            }
        }
        if (!tally.Succeeded(topology, commands, scratch, out failure)) return false;
        certificate = new(full, full ? minimumCore : double.NaN, errorBound, displaced);
        return true;
    }

    static void Vertices(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before, ReadOnlySpan<ModelVertex> after,
        ReadOnlySpan<CoverageCommandPose> commands, CoverageScratch scratch, ref CoverageTally tally, out int displaced)
    {
        displaced = 0;
        int stamp = scratch.NextStamp();
        for (int i = 0; i < after.Length; i++)
        {
            CoverageRefinedVertex record = topology.Vertices[i];
            Vector3 b = before[i].Position, a = after[i].Position;
            int slot = record.RegionIndex >= 0 && record.Strength > 0 ? scratch.RegionCommands[record.RegionIndex] : -1;
            if (b != a) displaced++;
            int group = record.PositionGroup;
            if (scratch.GroupStamps[group] != stamp) { scratch.GroupStamps[group] = stamp; scratch.GroupPositions[group] = a; }
            else if (scratch.GroupPositions[group] != a) tally.Note(CoverageFailureCode.FoldedPatch, slot);
            if (slot < 0)
            {
                if (!SameBytes(before[i], after[i])) tally.Note(CoverageFailureCode.FoldedPatch, slot);
                continue;
            }
            ref readonly CoverageCommandFrame frame = ref scratch.Frames[slot];
            ref readonly CoverageCommandPose command = ref commands[frame.Command];
            CoverageBoundedVector qb = frame.BoundedHostToCoverage.Apply(b), qa = frame.BoundedHostToCoverage.Apply(a);
            double low = CoverageIntervals.Add(new(command.MinZ, command.MinZ), new(frame.CapMarginUpper, frame.CapMarginUpper)).Max;
            double high = CoverageIntervals.Subtract(new(command.MaxZ, command.MaxZ), new(frame.CapMarginUpper, frame.CapMarginUpper)).Min;
            if (!qb.IsFinite || !qa.IsFinite || qb.Z.Min < low || qb.Z.Max > high || qa.Z.Min < low || qa.Z.Max > high)
                tally.Note(CoverageFailureCode.CapContact, slot);
            CoverageBoundedVector delta = CoverageBoundedVector.From(a) - CoverageBoundedVector.From(b);
            CoverageScalarBounds distance = a == b ? CoverageIntervals.Zero : frame.BoundedActor.ApplyLinear(delta).Length;
            if ((!distance.IsFinite || distance.Max > command.MaxWorldRecession)
                && !CoverageGeometry.SquaredDistanceWithinBudget(CoverageAffinePoint.Project(a, frame.ActorModel),
                    CoverageAffinePoint.Project(b, frame.ActorModel), command.MaxWorldRecession))
                tally.Note(CoverageFailureCode.RecessionExceeded, slot);
            if (topology.IsCoreVertex(i) && !CoverageSheetSide.Contains(command.Support, frame.HostToActualSheet.Apply(a)))
                tally.Note(CoverageFailureCode.CoreEscaped, slot);
        }
    }

    // Nondegenerate commanded faces with one projected orientation per command, and no changed face that collapses or
    // turns over relative to its unconformed pose.
    static void Faces(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before, ReadOnlySpan<ModelVertex> after,
        CoverageScratch scratch, Span<int> signs, ref CoverageTally tally)
    {
        signs.Clear();
        var indices = topology.Indices;
        for (int t = 0; t < topology.TriangleCount; t++)
        {
            int i0 = (int)indices[t * 3], i1 = (int)indices[t * 3 + 1], i2 = (int)indices[t * 3 + 2];
            CoverageRefinedTriangle record = topology.Triangles[t];
            int slot = record.RegionIndex >= 0 ? scratch.RegionCommands[record.RegionIndex] : -1;
            bool changed = before[i0].Position != after[i0].Position || before[i1].Position != after[i1].Position || before[i2].Position != after[i2].Position;
            scratch.TriangleFlags[t] = (byte)((changed ? Changed : 0) | (slot >= 0 ? Commanded : 0) | (Math.Max(slot, 0) << 2));
            if (changed)
            {
                CoverageVector b0 = CoverageVector.From(before[i0].Position), a0 = CoverageVector.From(after[i0].Position);
                CoverageVector e1 = CoverageVector.From(after[i1].Position) - a0, e2 = CoverageVector.From(after[i2].Position) - a0;
                CoverageVector normal = CoverageVector.Cross(e1, e2);
                CoverageVector original = CoverageVector.Cross(CoverageVector.From(before[i1].Position) - b0, CoverageVector.From(before[i2].Position) - b0);
                if (!(normal.Length > 1e-12 * e1.Length * e2.Length) || !(CoverageVector.Dot(normal, original) > 0))
                    tally.Note(CoverageFailureCode.FoldedPatch, slot);
            }
            if (slot < 0) continue;
            ref readonly CoverageCommandFrame frame = ref scratch.Frames[slot];
            CoverageVector p0 = frame.HostToCoverage.Apply(after[i0].Position), p1 = frame.HostToCoverage.Apply(after[i1].Position),
                p2 = frame.HostToCoverage.Apply(after[i2].Position);
            double area = CoverageTriangleGeometry.Orient(p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y);
            // A vertical, collapsed or overhanging face cannot be served by this directional mechanism.
            if (!(Math.Abs(area) > 1e-9 * (p1 - p0).Length * (p2 - p0).Length)) { tally.Note(CoverageFailureCode.FoldedPatch, slot); continue; }
            CoverageScalar exactArea = CoverageProjectedGeometry.Orientation(after[i0].Position, after[i1].Position, after[i2].Position, frame);
            if (!exactArea.IsFinite || exactArea.Sign == 0) { tally.Note(CoverageFailureCode.FoldedPatch, slot); continue; }
            int sign = exactArea.Sign;
            if (signs[slot] == 0) signs[slot] = sign;
            else if (signs[slot] != sign) tally.Note(CoverageFailureCode.FoldedPatch, slot);
        }
    }

    // Nonadjacent commanded faces must not overlap in their coverage XY projection, so the patch is an injective height field.
    static void Injectivity(CoverageRefinement topology, ReadOnlySpan<ModelVertex> after, CoverageScratch scratch, ReadOnlySpan<int> signs,
        Span<int> stack, ref CoverageTally tally)
    {
        var indices = topology.Indices;
        CoverageRefitBvh tree = scratch.Tree;
        bool any = false;
        for (int t = 0; t < topology.TriangleCount; t++)
        {
            if ((scratch.TriangleFlags[t] & Commanded) == 0) { tree.SetEmpty(t); continue; }
            tree.SetLeaf(t, ProjectedBox(topology, after, scratch, t)); any = true;
        }
        if (!any) return;
        tree.Refit();
        for (int t = 0; t < topology.TriangleCount; t++)
        {
            byte flags = scratch.TriangleFlags[t];
            if ((flags & Commanded) == 0) continue;
            int slot = flags >> 2;
            ref readonly CoverageCommandFrame frame = ref scratch.Frames[slot];
            var walk = new CoverageBvhWalk(tree.Nodes, stack, ProjectedBox(topology, after, scratch, t));
            while (walk.Next(out int u))
            {
                byte other = scratch.TriangleFlags[u];
                if (u <= t || (other & Commanded) == 0 || other >> 2 != slot) continue;
                if (CoverageProjectedGeometry.InteriorsOverlap(
                        after[(int)indices[t * 3]].Position, after[(int)indices[t * 3 + 1]].Position, after[(int)indices[t * 3 + 2]].Position,
                        after[(int)indices[u * 3]].Position, after[(int)indices[u * 3 + 1]].Position, after[(int)indices[u * 3 + 2]].Position,
                        frame, signs[slot]))
                    tally.Note(CoverageFailureCode.FoldedPatch, slot);
            }
        }
    }

    static CoverageBox ProjectedBox(CoverageRefinement topology, ReadOnlySpan<ModelVertex> after, CoverageScratch scratch, int t)
    {
        ref readonly CoverageCommandFrame frame = ref scratch.Frames[scratch.TriangleFlags[t] >> 2];
        var box = CoverageBox.Empty;
        for (int corner = 0; corner < 3; corner++)
        {
            CoverageBoundedVector p = frame.BoundedHostToCoverage.Apply(after[(int)topology.Indices[t * 3 + corner]].Position);
            box.Add(new(p.X.Min, p.Y.Min, 0)); box.Add(new(p.X.Max, p.Y.Max, 0));
        }
        box.Flatten();
        return box;
    }

    // Affected pairs must be separated or meet only on their common topology. Baseline touching, ambiguity and
    // penetration grant no waiver. An affected existing defect that cannot satisfy this proof refuses conservatively.
    static void HostIntersections(CoverageRefinement topology, ReadOnlySpan<ModelVertex> after,
        CoverageScratch scratch, Span<int> stack, ref CoverageTally tally)
    {
        var indices = topology.Indices;
        CoverageRefitBvh tree = scratch.Tree;
        bool any = false;
        for (int t = 0; t < topology.TriangleCount; t++)
        {
            tree.SetLeaf(t, ModelBox(after, indices, t));
            any |= (scratch.TriangleFlags[t] & Changed) != 0;
        }
        if (!any) return;
        tree.Refit();
        Span<CoverageVector> a = stackalloc CoverageVector[3], b = stackalloc CoverageVector[3];
        Span<int> ag = stackalloc int[3], bg = stackalloc int[3];
        for (int t = 0; t < topology.TriangleCount; t++)
        {
            byte flags = scratch.TriangleFlags[t];
            if ((flags & Changed) == 0) continue;
            var walk = new CoverageBvhWalk(tree.Nodes, stack, ModelBox(after, indices, t));
            while (walk.Next(out int u))
            {
                if (u == t || (scratch.TriangleFlags[u] & Changed) != 0 && u < t) continue;
                if (Separation(after, indices, t, u) > 0) continue;
                for (int i = 0; i < 3; i++)
                {
                    int ai = (int)indices[t * 3 + i], bi = (int)indices[u * 3 + i];
                    a[i] = CoverageVector.From(after[ai].Position); b[i] = CoverageVector.From(after[bi].Position);
                    ag[i] = topology.Vertices[ai].PositionGroup; bg[i] = topology.Vertices[bi].PositionGroup;
                }
                if (!CoverageContact.OnlyCommonTopology(a, b, ag, bg))
                    tally.Note(CoverageFailureCode.HostIntersection, (flags & Commanded) != 0 ? flags >> 2 : -1);
            }
        }
    }

    static CoverageBox ModelBox(ReadOnlySpan<ModelVertex> surface, ReadOnlySpan<uint> indices, int t)
    {
        var box = CoverageBox.Empty;
        for (int corner = 0; corner < 3; corner++) box.Add(CoverageVector.From(surface[(int)indices[t * 3 + corner]].Position));
        return box;
    }

    static double Separation(ReadOnlySpan<ModelVertex> surface, ReadOnlySpan<uint> indices, int t, int u) =>
        CoverageTriangleGeometry.SeparationLowerBound(CoverageVector.From(surface[(int)indices[t * 3]].Position),
            CoverageVector.From(surface[(int)indices[t * 3 + 1]].Position), CoverageVector.From(surface[(int)indices[t * 3 + 2]].Position),
            CoverageVector.From(surface[(int)indices[u * 3]].Position), CoverageVector.From(surface[(int)indices[u * 3 + 1]].Position),
            CoverageVector.From(surface[(int)indices[u * 3 + 2]].Position));

    // Valid only directly after Certify accepted this exact surface and TryBound accepted its captured passes. TryBound's
    // one frame write is RenderErrorBound, read only by Equipment's threshold for other commanded regions. Every other
    // check, every own-region threshold, the triangle flags and the certificate are therefore unchanged. A slot whose
    // bound does not exceed its construction bound keeps the threshold Certify already passed. Slots and triangles keep
    // Certify's order and tally, and own faces already passed, so the first failing slot and its count equal those of a
    // second full Certify.
    static bool RenderSeparation(CoverageRefinement topology, ReadOnlySpan<ModelVertex> after, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageScratch scratch, out CoverageFailure failure)
    {
        var tally = new CoverageTally();
        Span<int> stack = stackalloc int[64];
        double unusedCore = double.PositiveInfinity;
        for (int slot = 0; slot < scratch.ActiveCommands; slot++)
        {
            ref readonly CoverageCommandFrame frame = ref scratch.Frames[slot];
            if (!frame.Full || frame.RenderErrorBound <= frame.ConstructionErrorBound) continue;
            scratch.CertificationWitnessForTests?.Invoke(CoverageCertificationWork.RenderSeparation, slot);
            Equipment(topology, after, commands, scratch, slot, true, stack, ref unusedCore, ref tally);
            if (tally.Failed) break;
        }
        return tally.Succeeded(topology, commands, scratch, out failure);
    }

    // Every commanded face against one full-weight command's actual displayed solid in actor-relative world space. Its
    // own region keeps the authored clearance. Other commanded regions must at least stay certifiably apart from it.
    // otherRegionsOnly skips own faces, whose threshold no render bound changes.
    static void Equipment(CoverageRefinement topology, ReadOnlySpan<ModelVertex> after, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageScratch scratch, int slot, bool otherRegionsOnly, Span<int> stack, ref double minimumCore, ref CoverageTally tally)
    {
        ref readonly CoverageCommandFrame frame = ref scratch.Frames[slot];
        CoverageSupportSurface support = commands[frame.Command].Support;
        CoverageSourceTopology solid = support.Source;
        var indices = topology.Indices;
        for (int t = 0; t < topology.TriangleCount; t++)
        {
            byte flags = scratch.TriangleFlags[t];
            if ((flags & Commanded) == 0) continue;
            bool own = flags >> 2 == slot;
            if (own && otherRegionsOnly) continue;
            double threshold = own ? frame.Clearance : Math.Max(frame.ConstructionErrorBound, frame.RenderErrorBound);
            var box = CoverageBox.Empty;
            for (int corner = 0; corner < 3; corner++) box.Add(frame.HostToCoverage.Apply(after[(int)indices[t * 3 + corner]].Position));
            box.Expand(threshold / frame.SigmaMin + frame.Uncertainty + 1e-12 * (Math.Max(Math.Abs(box.MinX), Math.Abs(box.MaxX)) + 1));
            CoverageVector h0 = frame.ActorLinear.Apply(after[(int)indices[t * 3]].Position);
            CoverageVector h1 = frame.ActorLinear.Apply(after[(int)indices[t * 3 + 1]].Position);
            CoverageVector h2 = frame.ActorLinear.Apply(after[(int)indices[t * 3 + 2]].Position);
            bool core = own && topology.Triangles[t].Role == CoverageTriangleRole.Core;
            double nearest = threshold;
            var walk = new CoverageBvhWalk(support.SolidBvh.Nodes, stack, box);
            while (walk.Next(out int s))
            {
                CoverageSourceTriangle triangle = solid.Triangles[s];
                double bound = CoverageTriangleGeometry.SeparationLowerBound(h0, h1, h2, frame.EquipmentRelative.Apply(solid.Positions[triangle.A]),
                    frame.EquipmentRelative.Apply(solid.Positions[triangle.B]), frame.EquipmentRelative.Apply(solid.Positions[triangle.C]));
                nearest = Math.Min(nearest, bound);
            }
            if (nearest < threshold) tally.Note(CoverageFailureCode.EquipmentIntersection, slot);
            if (core) minimumCore = Math.Min(minimumCore, nearest);
        }
    }

    // Union over position groups of all commanded faces. Each component is certified clear of the solid boundary, so a
    // single winding number decides whether the whole component lies inside the solid.
    static void Components(CoverageRefinement topology, CoverageScratch scratch)
    {
        int[] parents = scratch.GroupParents;
        for (int g = 0; g < parents.Length; g++) parents[g] = g;
        var indices = topology.Indices;
        for (int t = 0; t < topology.TriangleCount; t++)
        {
            if ((scratch.TriangleFlags[t] & Commanded) == 0) continue;
            int a = topology.Vertices[(int)indices[t * 3]].PositionGroup;
            Union(parents, a, topology.Vertices[(int)indices[t * 3 + 1]].PositionGroup);
            Union(parents, a, topology.Vertices[(int)indices[t * 3 + 2]].PositionGroup);
        }
    }

    static int Find(int[] parents, int x)
    {
        while (parents[x] != x) { parents[x] = parents[parents[x]]; x = parents[x]; }
        return x;
    }

    static void Union(int[] parents, int a, int b)
    {
        a = Find(parents, a); b = Find(parents, b);
        if (a != b) parents[Math.Max(a, b)] = Math.Min(a, b);
    }

    static void Containment(CoverageRefinement topology, ReadOnlySpan<ModelVertex> after, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageScratch scratch, int slot, ref CoverageTally tally)
    {
        ref readonly CoverageCommandFrame frame = ref scratch.Frames[slot];
        CoverageSourceTopology solid = commands[frame.Command].Support.Source;
        int stamp = scratch.NextStamp();
        var indices = topology.Indices;
        for (int t = 0; t < topology.TriangleCount; t++)
        {
            if ((scratch.TriangleFlags[t] & Commanded) == 0) continue;
            int vertex = (int)indices[t * 3];
            int root = Find(scratch.GroupParents, topology.Vertices[vertex].PositionGroup);
            if (scratch.GroupStamps[root] == stamp) continue;
            scratch.GroupStamps[root] = stamp;
            CoverageVector point = frame.ActorLinear.Apply(after[vertex].Position);
            double sum = 0;
            for (int s = 0; s < solid.Triangles.Count; s++)
            {
                CoverageSourceTriangle triangle = solid.Triangles[s];
                sum += CoverageTriangleGeometry.SolidAngle(frame.EquipmentRelative.Apply(solid.Positions[triangle.A]) - point,
                    frame.EquipmentRelative.Apply(solid.Positions[triangle.B]) - point, frame.EquipmentRelative.Apply(solid.Positions[triangle.C]) - point);
            }
            // Off the surface the winding number of a closed solid is an integer. Only a certain zero is outside.
            if (!(Math.Abs(sum / (4 * Math.PI)) <= .25)) tally.Note(CoverageFailureCode.EquipmentIntersection, slot);
        }
    }

    // The convex-domain vertex certificates cover the complete affine face. Equipment has already proved rigorous
    // whole-face separation from every actual solid triangle, including the sheet. The continuous signed height gap
    // therefore cannot vanish anywhere on this connected face. One strict, outward-bounded side witness fixes its sign
    // everywhere. Euclidean clearance is certified separately by Equipment, never by this side witness.
    static void CoreSide(CoverageRefinement topology, ReadOnlySpan<ModelVertex> after, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageScratch scratch, int slot, ref CoverageTally tally)
    {
        ref readonly CoverageCommandFrame frame = ref scratch.Frames[slot];
        CoverageSupportSurface support = commands[frame.Command].Support;
        for (int t = 0; t < topology.TriangleCount; t++)
        {
            if (scratch.TriangleFlags[t] >> 2 != slot || (scratch.TriangleFlags[t] & Commanded) == 0
                || topology.Triangles[t].Role != CoverageTriangleRole.Core) continue;
            Vector3 point = after[(int)topology.Indices[t * 3]].Position;
            if (!CoverageSheetSide.Behind(support, frame.HostToActualSheet.Apply(point)))
                tally.Note(CoverageFailureCode.EquipmentIntersection, slot);
        }
    }

    static bool SameBytes(in ModelVertex a, in ModelVertex b) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<ModelVertex>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<ModelVertex>(in b)));
}

/// <summary>Certification work a test witness observes. Full is one complete Certify of a surface. RenderSeparation is
/// one full-weight command slot whose other-region threshold the captured render bound raised.</summary>
internal enum CoverageCertificationWork
{
    Full, RenderSeparation,
}
