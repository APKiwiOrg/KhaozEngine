using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Render3D.CoverageReuseResearch;

/// <summary>Static counts of one fixture: refined topology, groups, regions and every support.</summary>
internal sealed record CoverageResearchTopologyReport(int SourceVertices, int SourceTriangles, int FinalVertices, int FinalTriangles,
    int PositionGroups, int ShadingGroups, int SourceAliases, int EdgeSplits, int Regions, int RegionSourceTriangles,
    int CoreSourceTriangles, int CollarSourceTriangles, int RegionFinalTriangles, int CoreFinalTriangles, string RefinementLevels,
    CoverageResearchSupportReport[] Supports, int[] PartTriangles, int[] PartComponents, CoverageResearchSolidReport[] PartSolids);

/// <summary>Why a displayed part is or is not an embedded closed manifold, counted with the solid certificate's own
/// welding and contact rule. Contacts are nonadjacent triangle pairs, split by whether both lie in one component.</summary>
internal sealed record CoverageResearchSolidReport(int Triangles, int WeldedVertices, int DegenerateTriangles, int OpenEdges,
    int OverSharedEdges, int MisorientedEdges, int NonCycleLinks, int ContactsWithinComponents, int ContactsBetweenComponents,
    int Components);

internal sealed record CoverageResearchSupportReport(uint Command, int InwardTriangles, int BoundaryEdges, int SolidTriangles,
    bool InwardOfClosedSolid, double MaxSlope, float CollarWidth, float MinZ, float MaxZ, float Clearance, float RecessionCap,
    double Nc2Reserve);

/// <summary>One anchor's exact verdict and independent geometry. Lengths are world metres.</summary>
internal sealed record CoverageResearchAnchorReport(
    string Key, bool HasPrevious, bool Accepted, string Stage, string Code, uint FailedCommand, int FailureCount, int FailureLimit,
    bool Constructed, int DisplacedVertices, double MinimumCoreClearance, bool FullCoreClearance,
    double MaxRequestedRecession, double RecessionCap, double AvailableSlack, double Nc2Reserve, double MinimumSheetGap,
    int ActiveVertices, int ActiveTriangles, double ActiveCoreArea, int CoreVerticesOutsideDomain, int CapViolations,
    int ProtectedVertices, int ProtectedCopyMismatches,
    int RegionEquipmentPairsBefore, int ProtectedEquipmentPairsBefore, int[] ProtectedEquipmentPairsByComponent,
    int RegionEquipmentPairsAfter, int HostSelfPairsNearEquipment, int HostSelfPairsTouchingRegion,
    SortedDictionary<int, int> ProtectedCrossingJoints);

/// <summary>Runs the exact oracle and the independent diagnostics for every anchor of one fixture.</summary>
internal sealed class CoverageResearchDiagnostics
{
    readonly CoverageResearchFixture fixture;
    readonly CoverageOracle oracle;
    readonly CoverageReferenceSkin skin;
    readonly CoverageReferenceScratch scratch;
    readonly Matrix4x4[] composed;
    readonly ModelVertex[] posed, final;
    readonly bool[] regionTriangle;
    readonly int[][] components;
    readonly (int Part, int First, int Count)[] componentRanges;

    internal CoverageResearchSurfaces Surfaces { get; }

    internal CoverageResearchDiagnostics(CoverageResearchFixture fixture)
    {
        this.fixture = fixture;
        var topology = fixture.Topology;
        oracle = new CoverageOracle(topology, fixture.InverseBind, Math.Max(1, fixture.Commands.Length));
        skin = new CoverageReferenceSkin(topology.Source, fixture.InverseBind.Length);
        scratch = new CoverageReferenceScratch(topology, Math.Max(1, fixture.Commands.Length));
        composed = new Matrix4x4[fixture.InverseBind.Length];
        posed = new ModelVertex[topology.SourceVertexCount];
        final = new ModelVertex[topology.VertexCount];
        Surfaces = new(topology);
        regionTriangle = new bool[topology.Source.Triangles.Count];
        foreach (var t in topology.Triangles) if (t.RegionIndex >= 0) regionTriangle[t.SourceTriangle] = true;
        // Equipment witnesses: each part's displayed source, split into connected components by welded position.
        var all = new List<int[]>();
        var ranges = new List<(int, int, int)>();
        for (int p = 0; p < fixture.Parts.Length; p++)
            foreach (var component in Components(fixture.Parts[p]))
            {
                ranges.Add((p, all.Count, 1));
                all.Add(component);
            }
        components = all.ToArray();
        componentRanges = ranges.ToArray();
    }

    internal int ComponentCount => components.Length;

    internal CoverageResearchTopologyReport Topology()
    {
        var t = fixture.Topology;
        var regionSource = t.Map.Regions.SelectMany(r => r.Triangles).ToArray();
        return new(t.SourceVertexCount, t.Source.Triangles.Count, t.VertexCount, t.TriangleCount, t.PositionGroupCount,
            t.ShadingGroupCount, t.SourceAliases.Count, t.EdgeSplits.Count, t.Map.Regions.Count,
            regionSource.Sum(r => r.Triangles.Count), regionSource.Where(r => r.Role == CoverageTriangleRole.Core).Sum(r => r.Triangles.Count),
            regionSource.Where(r => r.Role == CoverageTriangleRole.Collar).Sum(r => r.Triangles.Count),
            t.Triangles.Count(x => x.RegionIndex >= 0), t.Triangles.Count(x => x.RegionIndex >= 0 && x.Role == CoverageTriangleRole.Core),
            string.Join(',', t.Map.Refinement.Select(r => r.Level).Distinct().Order()),
            fixture.Commands.Select(c => new CoverageResearchSupportReport(c.Definition.Id, c.Support.InwardTriangles.Count,
                c.Support.Boundary.Count, c.Support.Source.Triangles.Count, c.Support.InwardOfClosedSolid, c.Support.MaxSlope,
                c.Definition.CollarWidth, c.Definition.MinZ, c.Definition.MaxZ, c.Definition.Clearance, c.Definition.MaxWorldRecession,
                CoverageFrames.MarginFraction * c.Definition.Clearance)).ToArray(),
            fixture.Parts.Select(p => p.Triangles.Count).ToArray(),
            fixture.Parts.Select((_, p) => componentRanges.Count(r => r.Part == p)).ToArray(),
            fixture.Parts.Select(Solid).ToArray());
    }

    internal static CoverageResearchSolidReport Solid(CoverageSourceTopology source)
    {
        var ids = new Dictionary<Vector3, int>();
        var weld = source.Positions.ToArray().Select(p => ids.TryGetValue(p, out int id) ? id : ids[p] = ids.Count).ToArray();
        int degenerate = 0;
        var edges = new Dictionary<(int, int), List<int>>();
        var links = new Dictionary<int, List<(int, int)>>();
        foreach (var f in source.Triangles)
        {
            int a = weld[f.A], b = weld[f.B], c = weld[f.C];
            if (a == b || b == c || c == a
                || Vector3.Cross(source.Positions[f.B] - source.Positions[f.A], source.Positions[f.C] - source.Positions[f.A]).LengthSquared() == 0)
            { degenerate++; continue; }
            foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
            {
                var key = (Math.Min(x, y), Math.Max(x, y));
                if (!edges.TryGetValue(key, out var list)) edges.Add(key, list = new());
                list.Add(x < y ? 1 : -1);
            }
            foreach (var (v, x, y) in new[] { (a, b, c), (b, c, a), (c, a, b) })
            {
                if (!links.TryGetValue(v, out var list)) links.Add(v, list = new());
                list.Add((x, y));
            }
        }
        int open = edges.Values.Count(e => e.Count == 1), over = edges.Values.Count(e => e.Count > 2);
        int misoriented = edges.Values.Count(e => e.Count == 2 && e[0] == e[1]);
        int nonCycle = links.Values.Count(link => !Cycle(link));
        var component = new int[source.Triangles.Count];
        var groups = Components(source).ToArray();
        for (int g = 0; g < groups.Length; g++) foreach (int t in groups[g]) component[t] = g;
        int within = 0, between = 0;
        Span<CoverageVector> first = stackalloc CoverageVector[3], second = stackalloc CoverageVector[3];
        Span<int> firstIds = stackalloc int[3], secondIds = stackalloc int[3];
        for (int t = 0; t < source.Triangles.Count; t++)
        {
            Fill(t, first, firstIds);
            for (int u = t + 1; u < source.Triangles.Count; u++)
            {
                Fill(u, second, secondIds);
                if (CoverageContact.OnlyCommonTopology(first, second, firstIds, secondIds)) continue;
                if (component[t] == component[u]) within++; else between++;
            }
        }
        return new(source.Triangles.Count, ids.Count, degenerate, open, over, misoriented, nonCycle, within, between, groups.Length);

        void Fill(int t, Span<CoverageVector> points, Span<int> group)
        {
            var f = source.Triangles[t];
            points[0] = CoverageVector.From(source.Positions[f.A]); points[1] = CoverageVector.From(source.Positions[f.B]);
            points[2] = CoverageVector.From(source.Positions[f.C]);
            group[0] = weld[f.A]; group[1] = weld[f.B]; group[2] = weld[f.C];
        }
    }

    static bool Cycle(List<(int A, int B)> link)
    {
        var graph = new Dictionary<int, List<int>>();
        foreach (var (a, b) in link)
        {
            if (!graph.TryGetValue(a, out var x)) graph.Add(a, x = new());
            if (!graph.TryGetValue(b, out var y)) graph.Add(b, y = new());
            x.Add(b); y.Add(a);
        }
        if (graph.Values.Any(n => n.Count != 2)) return false;
        var reached = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(graph.Keys.First());
        while (pending.TryPop(out int v))
            if (reached.Add(v)) foreach (int n in graph[v]) pending.Push(n);
        return reached.Count == graph.Count;
    }

    internal CoverageResearchAnchorReport Run(CoverageResearchAnchor anchor)
    {
        var frame = anchor.Current;
        Matrix4x4[] palette = frame.PaletteMatrices;
        Matrix4x4 model = frame.ModelMatrix;
        var commands = fixture.Poses(frame);
        bool accepted = oracle.TryCheck(palette, model, commands, out var certificate, out var failure);

        // The reference before surface, independent of whether construction succeeds.
        var topology = fixture.Topology;
        scratch.Arithmetic.Reset();
        scratch.UniformArithmetic.Reset();
        for (int i = 0; i < palette.Length; i++) composed[i] = CoverageReferenceSkinning.Compose(palette[i], fixture.InverseBind[i]);
        if (!CoverageReferenceSkinning.TrySkin(scratch.Arithmetic, skin, topology.Source, composed, posed, out _)
            || !CoverageReferenceConstructor.TryInterpolate(topology, posed, scratch, out _))
            throw new InvalidOperationException($"Reference skinning refused {anchor.Key}.");
        var before = scratch.Before.ToArray();
        bool constructed = oracle.LastConstructedVertices.Length == final.Length;
        if (constructed) oracle.LastConstructedVertices.CopyTo(final);
        bool prepared = CoverageReferenceFrames.TryPrepare(topology, model, commands, scratch, out _);
        Surfaces.Add(anchor.Key, before, final, constructed, scratch, prepared);

        // Requested recession at full weight over every positive-strength refined vertex, with the actual support.
        double maxRequest = 0, cap = double.PositiveInfinity, reserve = double.PositiveInfinity, gap = double.PositiveInfinity;
        int active = 0, outside = 0, capViolations = 0;
        var activeVertex = new bool[topology.VertexCount];
        foreach (var c in fixture.Commands)
        {
            var d = c.Definition;
            cap = Math.Min(cap, d.MaxWorldRecession);
            reserve = Math.Min(reserve, CoverageFrames.MarginFraction * d.Clearance);
            Matrix4x4 coverageWorld = c.Support.CoverageLocal * frame.PartWorld(c.PartIndex);
            if (!Matrix4x4.Invert(coverageWorld, out Matrix4x4 toCoverage)) throw new InvalidOperationException("Singular coverage frame.");
            double zScale = new Vector3(coverageWorld.M31, coverageWorld.M32, coverageWorld.M33).Length();
            double clearance = d.Clearance / zScale;
            var domain = new CoverageResearchDomain(c.Support);
            var core = CoreVertices(topology, c.RegionIndex);
            for (int v = 0; v < topology.VertexCount; v++)
            {
                var info = topology.Vertices[v];
                if (info.RegionIndex != c.RegionIndex || info.Strength <= 0) continue;
                Vector3 q = Vector3.Transform(Vector3.Transform(before[v].Position, model), toCoverage);
                if (q.Z < d.MinZ || q.Z > d.MaxZ) capViolations++;
                double spatial, height;
                if (c.Support.TryGetHeight(q.X, q.Y, out height))
                {
                    spatial = 1;
                    // World distance the vertex stands behind the clearance target. Negative needs recession.
                    gap = Math.Min(gap, (height - clearance - q.Z) * zScale);
                }
                else
                {
                    if (core[v]) outside++;
                    double distance = domain.Distance(q.X, q.Y);
                    if (distance >= d.CollarWidth) continue;
                    // Nearest boundary height continuation, read just inside the nearest boundary point.
                    if (!NearestBoundaryHeight(c.Support, q.X, q.Y, out height)) continue;
                    double u = Math.Clamp(1 - distance / d.CollarWidth, 0, 1);
                    spatial = u * u * (3 - 2 * u);
                }
                double request = Math.Max(0, q.Z - (height - clearance)) * (core[v] ? 1 : info.Strength) * spatial * zScale;
                if (request <= 0) continue;
                activeVertex[v] = true;
                active++;
                maxRequest = Math.Max(maxRequest, request);
            }
        }
        int activeTriangles = 0;
        double activeCoreArea = 0;
        var indices = topology.Indices;
        for (int t = 0; t < topology.TriangleCount; t++)
        {
            int a = (int)indices[t * 3], b = (int)indices[t * 3 + 1], c = (int)indices[t * 3 + 2];
            if (!(activeVertex[a] || activeVertex[b] || activeVertex[c])) continue;
            activeTriangles++;
            if (topology.Triangles[t].Role == CoverageTriangleRole.Core && activeVertex[a] && activeVertex[b] && activeVertex[c])
                activeCoreArea += 0.5 * Vector3.Cross(before[b].Position - before[a].Position, before[c].Position - before[a].Position).Length();
        }

        // Protected copies: every refined vertex outside positive strength keeps its reference position bits.
        int protectedVertices = 0, mismatches = 0;
        if (constructed)
            for (int v = 0; v < topology.VertexCount; v++)
            {
                var info = topology.Vertices[v];
                if (info.RegionIndex >= 0 && info.Strength > 0) continue;
                protectedVertices++;
                if (!Same(before[v].Position, final[v].Position)) mismatches++;
            }

        // Independent triangle intersections in world space, double precision.
        var hostBefore = SourceWorld(topology, posed, model);
        var equipment = components.Select((tris, k) => World(fixture.Parts[componentRanges[k].Part], tris,
            frame.PartWorld(componentRanges[k].Part))).ToArray();
        int regionBefore = 0, protectedBefore = 0;
        var byComponent = new int[components.Length];
        var joints = new SortedDictionary<int, int>();
        var source = topology.Source;
        for (int t = 0; t < source.Triangles.Count; t++)
        {
            var ht = Tri(hostBefore, source.Triangles[t]);
            for (int k = 0; k < equipment.Length; k++)
                foreach (var et in equipment[k])
                    if (Intersects(ht, et))
                    {
                        if (regionTriangle[t]) regionBefore++;
                        else
                        {
                            protectedBefore++;
                            byComponent[k]++;
                            int joint = Dominant(source.Influences[source.Triangles[t].A]);
                            joints[joint] = joints.GetValueOrDefault(joint) + 1;
                        }
                    }
        }
        int regionAfter = 0;
        if (constructed)
        {
            var finalWorld = final.Select(v => Vector3.Transform(v.Position, model)).ToArray();
            for (int t = 0; t < topology.TriangleCount; t++)
            {
                if (topology.Triangles[t].RegionIndex < 0) continue;
                var ft = (P(finalWorld[indices[t * 3]]), P(finalWorld[indices[t * 3 + 1]]), P(finalWorld[indices[t * 3 + 2]]));
                foreach (var component in equipment)
                    foreach (var et in component)
                        if (Intersects(ft, et)) regionAfter++;
            }
        }
        var (near, touching) = HostSelfIntersections(topology, hostBefore, equipment);

        string stage = accepted ? "None" : failure.Stage.ToString();
        string code = accepted ? "None" : failure.Failure.Code.ToString();
        double slack = cap - maxRequest;
        return new(anchor.Key, anchor.Previous is not null, accepted, stage, code, accepted ? 0 : failure.Failure.CommandId,
            accepted ? 0 : failure.Failure.Count, accepted ? 0 : failure.Failure.Limit, constructed, certificate.DisplacedVertices,
            certificate.MinimumCoreClearance, certificate.FullCoreClearance, maxRequest, cap, slack, reserve, gap, active, activeTriangles,
            activeCoreArea, outside, capViolations, protectedVertices, mismatches, regionBefore, protectedBefore, byComponent,
            regionAfter, near, touching, joints);
    }

    static int Dominant(CoverageSkinInfluence influence)
    {
        int best = 0;
        for (int k = 1; k < 4; k++) if (influence.Weights[k] > influence.Weights[best]) best = k;
        return (int)influence.Joints[best];
    }

    static bool[] CoreVertices(CoverageRefinement topology, int region)
    {
        var core = new bool[topology.VertexCount];
        var indices = topology.Indices;
        for (int t = 0; t < topology.TriangleCount; t++)
            if (topology.Triangles[t].RegionIndex == region && topology.Triangles[t].Role == CoverageTriangleRole.Core)
                for (int k = 0; k < 3; k++) core[indices[t * 3 + k]] = true;
        return core;
    }

    static bool NearestBoundaryHeight(CoverageSupportSurface support, double x, double y, out double height)
    {
        height = 0;
        var loop = support.BoundarySourceVertices.ToArray();
        double best = double.PositiveInfinity;
        bool found = false;
        for (int i = 0; i < loop.Length; i++)
        {
            var a = support.PrecisePositions[loop[i]];
            var b = support.PrecisePositions[loop[(i + 1) % loop.Length]];
            double ex = b.X - a.X, ey = b.Y - a.Y, l2 = ex * ex + ey * ey;
            double t = l2 > 0 ? Math.Clamp(((x - a.X) * ex + (y - a.Y) * ey) / l2, 0, 1) : 0;
            double px = a.X + t * ex, py = a.Y + t * ey, d = (px - x) * (px - x) + (py - y) * (py - y);
            if (d >= best) continue;
            best = d;
            height = a.Z + t * (b.Z - a.Z);
            found = true;
        }
        return found;
    }

    static bool Same(Vector3 a, Vector3 b) => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
        && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
        && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);

    // Pre-existing host self-intersection near the equipment: nonadjacent posed source triangles that cross, both inside
    // the equipment bounds grown by 5 cm. Adjacency is any shared welded position.
    (int Near, int TouchingRegion) HostSelfIntersections(CoverageRefinement topology, Vector3d[] host, (Vector3d, Vector3d, Vector3d)[][] equipment)
    {
        var bounds = Bounds(equipment.SelectMany(c => c).SelectMany(t => new[] { t.Item1, t.Item2, t.Item3 }), 0.05);
        var source = topology.Source;
        var near = new List<int>();
        for (int t = 0; t < source.Triangles.Count; t++)
        {
            var tri = Tri(host, source.Triangles[t]);
            if (Overlaps(Bounds([tri.Item1, tri.Item2, tri.Item3], 0), bounds)) near.Add(t);
        }
        var key = new Dictionary<Vector3, int>();
        int Id(int v) { var p = source.Positions[v]; if (!key.TryGetValue(p, out int id)) key.Add(p, id = key.Count); return id; }
        var ids = near.ToDictionary(t => t, t => new[] { Id(source.Triangles[t].A), Id(source.Triangles[t].B), Id(source.Triangles[t].C) });
        int count = 0, touching = 0;
        for (int i = 0; i < near.Count; i++)
            for (int j = i + 1; j < near.Count; j++)
            {
                int a = near[i], b = near[j];
                if (ids[a].Intersect(ids[b]).Any()) continue;
                if (!Intersects(Tri(host, source.Triangles[a]), Tri(host, source.Triangles[b]))) continue;
                count++;
                if (regionTriangle[a] || regionTriangle[b]) touching++;
            }
        return (count, touching);
    }

    static Vector3d[] SourceWorld(CoverageRefinement topology, ModelVertex[] posed, Matrix4x4 model) =>
        posed.Select(v => P(Vector3.Transform(v.Position, model))).ToArray();

    static (Vector3d, Vector3d, Vector3d)[] World(CoverageSourceTopology part, int[] triangles, Matrix4x4 world) =>
        triangles.Select(t => (P(Vector3.Transform(part.Positions[part.Triangles[t].A], world)),
            P(Vector3.Transform(part.Positions[part.Triangles[t].B], world)), P(Vector3.Transform(part.Positions[part.Triangles[t].C], world))))
        .ToArray();

    static (Vector3d, Vector3d, Vector3d) Tri(Vector3d[] p, CoverageSourceTriangle t) => (p[t.A], p[t.B], p[t.C]);

    static Vector3d P(Vector3 v) => new(v.X, v.Y, v.Z);

    // Connected components of a displayed source by exactly welded positions, as triangle index lists.
    static IEnumerable<int[]> Components(CoverageSourceTopology part)
    {
        var parent = new Dictionary<Vector3, Vector3>();
        Vector3 Find(Vector3 x) { while (parent.TryGetValue(x, out var p) && p != x) x = p; return x; }
        void Join(Vector3 a, Vector3 b) { parent.TryAdd(a, a); parent.TryAdd(b, b); parent[Find(a)] = Find(b); }
        foreach (var t in part.Triangles)
        {
            Join(part.Positions[t.A], part.Positions[t.B]);
            Join(part.Positions[t.B], part.Positions[t.C]);
        }
        return part.Triangles.Select((t, i) => (Root: Find(part.Positions[t.A]), i)).GroupBy(x => x.Root)
            .OrderBy(g => g.Min(x => x.i)).Select(g => g.Select(x => x.i).ToArray());
    }

    static (Vector3d Min, Vector3d Max) Bounds(IEnumerable<Vector3d> points, double grow)
    {
        var min = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = new Vector3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        foreach (var p in points) { min = Vector3d.Min(min, p); max = Vector3d.Max(max, p); }
        return (min - new Vector3d(grow, grow, grow), max + new Vector3d(grow, grow, grow));
    }

    static bool Overlaps((Vector3d Min, Vector3d Max) a, (Vector3d Min, Vector3d Max) b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y && a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;

    // Two triangles intersect when an edge of either crosses the other, or they touch.
    internal static bool Intersects((Vector3d, Vector3d, Vector3d) s, (Vector3d, Vector3d, Vector3d) t)
    {
        if (!Overlaps(Bounds([s.Item1, s.Item2, s.Item3], 0), Bounds([t.Item1, t.Item2, t.Item3], 0))) return false;
        return Edge(s.Item1, s.Item2, t) || Edge(s.Item2, s.Item3, t) || Edge(s.Item3, s.Item1, t)
            || Edge(t.Item1, t.Item2, s) || Edge(t.Item2, t.Item3, s) || Edge(t.Item3, t.Item1, s);
    }

    static bool Edge(Vector3d p, Vector3d q, (Vector3d A, Vector3d B, Vector3d C) t)
    {
        Vector3d n = Vector3d.Cross(t.B - t.A, t.C - t.A);
        double dp = Vector3d.Dot(p - t.A, n), dq = Vector3d.Dot(q - t.A, n);
        if (dp > 0 && dq > 0 || dp < 0 && dq < 0 || dp == 0 && dq == 0) return false;
        Vector3d x = p + (q - p) * (dp / (dp - dq));
        double ab = Vector3d.Dot(Vector3d.Cross(t.B - t.A, x - t.A), n), bc = Vector3d.Dot(Vector3d.Cross(t.C - t.B, x - t.B), n),
            ca = Vector3d.Dot(Vector3d.Cross(t.A - t.C, x - t.C), n);
        return ab >= 0 && bc >= 0 && ca >= 0;
    }
}

/// <summary>A small double vector for the independent diagnostics.</summary>
internal readonly record struct Vector3d(double X, double Y, double Z)
{
    public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3d operator *(Vector3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    internal static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    internal static Vector3d Cross(Vector3d a, Vector3d b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    internal static Vector3d Min(Vector3d a, Vector3d b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    internal static Vector3d Max(Vector3d a, Vector3d b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
}
