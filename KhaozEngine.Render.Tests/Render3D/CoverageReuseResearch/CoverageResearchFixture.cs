using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Render3D.CoverageReuseResearch;

/// <summary>One bound command: its definition, compiled actual support and the manifest part it rides.</summary>
internal sealed record CoverageResearchCommand(EquipmentCoverageCommand Definition, CoverageSupportSurface Support, int PartIndex,
    int RegionIndex);

/// <summary>A loaded research fixture: the exact host topology, inverse binds, every part's displayed source as a
/// geometry witness, bound commands and anchors. Holds no assertion and writes nothing.</summary>
internal sealed class CoverageResearchFixture
{
    CoverageResearchFixture(string name, CoverageRefinement topology, Matrix4x4[] inverseBind, CoverageSourceTopology[] parts,
        CoverageResearchCommand[] commands, CoverageResearchAnchor[] anchors, string hostMapSha, string coverageSha,
        CoverageResearchProposalReport? proposal)
    {
        Name = name; Topology = topology; InverseBind = inverseBind; Parts = parts; Commands = commands; Anchors = anchors;
        HostMapSemanticSha256 = hostMapSha; CoverageSemanticSha256 = coverageSha; Proposal = proposal;
    }

    internal string Name { get; }
    internal CoverageRefinement Topology { get; }
    internal Matrix4x4[] InverseBind { get; }
    internal CoverageSourceTopology[] Parts { get; }
    internal CoverageResearchCommand[] Commands { get; }
    internal CoverageResearchAnchor[] Anchors { get; }
    internal string HostMapSemanticSha256 { get; }
    internal string CoverageSemanticSha256 { get; }
    internal CoverageResearchProposalReport? Proposal { get; }

    /// <summary>An in-memory fixture, for public synthetic controls built by code.</summary>
    internal static CoverageResearchFixture Create(string name, CoverageRefinement topology, Matrix4x4[] inverseBind,
        CoverageSourceTopology[] parts, CoverageResearchCommand[] commands, CoverageResearchAnchor[] anchors, string coverageSha) =>
        new(name, topology, inverseBind, parts, commands, anchors, HostCoverageMapJson.SemanticSha256(topology.Map), coverageSha, null);

    internal static CoverageResearchFixture Load(CoverageResearchManifest manifest)
    {
        byte[] host = manifest.Host.Read();
        byte[] attachment = manifest.Attachment.Read();
        var parts = manifest.Parts.Select(p => GltfLoader.LoadCoverageSource(p.Glb.Read(), $"part-{p.Id}")).ToArray();
        var anchors = CoverageResearchAnchors.Read(manifest.Anchors.Read());
        HostCoverageMap map;
        EquipmentCoverageDefinition coverage;
        CoverageResearchProposalReport? proposal = null;
        if (manifest.Proposal is { } settings)
        {
            int partIndex = Array.FindIndex(manifest.Parts, p => p.Id == settings.PartId);
            if (partIndex < 0) throw new InvalidDataException("Proposal names an absent part.");
            var anchor = anchors.SingleOrDefault(a => a.Key == settings.ReferenceAnchor)
                ?? throw new InvalidDataException("Proposal reference anchor is absent.");
            (map, coverage, proposal) = CoverageResearchProposalBuilder.Propose(host, manifest.Host.Sha256, attachment,
                parts[partIndex], partIndex, anchor.Current, settings);
            Directory.CreateDirectory(settings.OutputDirectory);
            File.WriteAllBytes(Path.Combine(settings.OutputDirectory, "proposed-host-map.json"), HostCoverageMapJson.Write(map));
            File.WriteAllBytes(Path.Combine(settings.OutputDirectory, "proposed-equipment-coverage.json"), EquipmentCoverageJson.Write(coverage));
        }
        else
        {
            map = HostCoverageMapJson.Read(manifest.HostMap!.Read(), "host-map");
            coverage = EquipmentCoverageJson.Read(manifest.Coverage!.Read(), "equipment-coverage");
        }
        string attachmentSha = manifest.Attachment.Sha256;
        if (coverage.AttachmentSha256 != attachmentSha) throw new InvalidDataException("Coverage companion names another attachment.");
        if (coverage.HostMapSha256 != HostCoverageMapJson.SemanticSha256(map)) throw new InvalidDataException("Coverage companion names another host map.");
        var skinned = GltfLoader.LoadSkinnedPartsWithMaterials(host, "host", map);
        var source = skinned[0].Mesh.Coverage!.Source;
        var topology = CoverageRefinement.Build(source, map);
        var commands = coverage.Commands.Select(command =>
        {
            int partIndex = Array.FindIndex(manifest.Parts, p => p.Id == command.PartId);
            if (partIndex < 0) throw new InvalidDataException($"Command {command.Id} names an absent part.");
            int region = map.Regions.Select((r, i) => (r, i)).Single(pair => pair.r.Key == command.HostRegion).i;
            var ranges = command.InwardSupport.Select(r => new CoverageTriangleRange(r.Mesh, r.Primitive, r.FirstTriangle, r.Count)).ToArray();
            return new CoverageResearchCommand(command, CoverageSupportSurface.Compile(parts[partIndex], ranges, command.CoverageLocal),
                partIndex, region);
        }).ToArray();
        return new(manifest.Fixture, topology, skinned[0].Mesh.InverseBind, parts, commands, anchors,
            HostCoverageMapJson.SemanticSha256(map), EquipmentCoverageJson.SemanticSha256(coverage), proposal);
    }

    /// <summary>Command poses for one frame. EquipmentWorld is the part's draw matrix, CoverageWorld the canonical
    /// composition Support.CoverageLocal * EquipmentWorld.</summary>
    internal CoverageCommandPose[] Poses(CoverageResearchFrame frame) => Commands.Select(c =>
    {
        Matrix4x4 world = frame.PartWorld(c.PartIndex);
        var d = c.Definition;
        return new CoverageCommandPose(d.Id, 1, Topology, c.RegionIndex, c.Support, world, c.Support.CoverageLocal * world,
            d.MinZ, d.MaxZ, d.CollarWidth, d.Clearance, d.MaxWorldRecession);
    }).ToArray();
}

/// <summary>What a proposal selected, for owner review. Counts only.</summary>
internal sealed record CoverageResearchProposalReport(int SupportTriangles, int SupportRanges, int PatchTriangles, int CoreTriangles,
    int CollarTriangles, int ZeroStrengthVertices, int SurfaceVertices, int WeldedAliases, string ReferenceAnchor,
    int OutwardTrianglesInsideDomain, double LargestCoreInset);

/// <summary>Generic footprint proposal of one region and one command. It reads host topology from the GLB with the same
/// ordinals and raw influences the coverage importer records, welds equal rest positions with equal normalized
/// influences, and selects geometry only by projection onto the actual support. No body, bone or item rule exists.</summary>
internal static class CoverageResearchProposalBuilder
{
    internal static (HostCoverageMap, EquipmentCoverageDefinition, CoverageResearchProposalReport) Propose(byte[] hostGlb,
        string hostSha, byte[] attachment, CoverageSourceTopology part, int partIndex, CoverageResearchFrame reference,
        CoverageResearchProposal settings)
    {
        Matrix4x4 coverageLocal = CoverageResearchAnchors.Matrix(settings.CoverageLocal);
        var supportRanges = SupportRanges(part, settings.SupportMesh, coverageLocal, settings.InwardCosine);
        var support = CoverageSupportSurface.Compile(part, supportRanges, coverageLocal);
        var host = HostGeometry.Read(hostGlb);
        Matrix4x4 coverageWorld = coverageLocal * reference.PartWorld(partIndex);
        if (!Matrix4x4.Invert(coverageWorld, out Matrix4x4 toCoverage)) throw new InvalidDataException("Singular coverage frame.");
        Matrix4x4[] skin = Skinning(GltfLoader.LoadSkinnedPartsWithMaterials(hostGlb, "host")[0].Mesh.InverseBind,
            reference.PaletteMatrices, reference.ModelMatrix);
        var q = host.Positions.Select((p, i) => Vector3.Transform(host.Skin(i, p, skin), toCoverage)).ToArray();
        var domain = new CoverageResearchDomain(support);

        // Eligible: outward facing in coverage, every corner within the footprint margin and the depth window.
        double sheetFloor = Enumerable.Range(0, support.PrecisePositions.Length).Where(i => support.InwardSourceIndices.ToArray()
            .Any(t => part.Triangles[t].A == i || part.Triangles[t].B == i || part.Triangles[t].C == i))
            .Min(i => support.PrecisePositions[i].Z);
        bool[] eligible = host.Triangles.Select(t =>
        {
            Vector3 a = q[t.A], b = q[t.B], c = q[t.C];
            if (Vector3.Cross(b - a, c - a).Z <= 0) return false;
            return new[] { a, b, c }.All(p => domain.Distance(p.X, p.Y) <= settings.PatchMargin && p.Z >= sheetFloor - settings.PatchDepth);
        }).ToArray();
        int seed = -1;
        double best = double.PositiveInfinity;
        Vector2 centre = domain.Centroid;
        for (int t = 0; t < eligible.Length; t++)
        {
            if (!eligible[t]) continue;
            var tri = host.Triangles[t];
            Vector3 c = (q[tri.A] + q[tri.B] + q[tri.C]) / 3;
            double d = Vector2.Distance(new Vector2(c.X, c.Y), centre) - 1e-3 * c.Z;
            if (d < best) { best = d; seed = t; }
        }
        if (seed < 0) throw new InvalidDataException("No host triangle lies in the proposed footprint.");
        var patch = host.Component(seed, eligible);

        // Boundary vertices take zero strength. Core needs every corner inside the domain by the inset, none on the boundary.
        var edgeUse = new Dictionary<(int, int), int>();
        foreach (int t in patch)
            foreach (var (a, b) in host.Edges(t))
                edgeUse[(Math.Min(a, b), Math.Max(a, b))] = edgeUse.GetValueOrDefault((Math.Min(a, b), Math.Max(a, b))) + 1;
        var boundary = edgeUse.Where(e => e.Value == 1).SelectMany(e => new[] { e.Key.Item1, e.Key.Item2 }).ToHashSet();
        bool Core(int t) => host.SurfaceCorners(t).All(s => !boundary.Contains(s))
            && host.Corners(t).All(v => domain.Inside(q[v].X, q[v].Y, settings.CoreInset));
        var roles = patch.ToDictionary(t => t, t => Core(t) ? CoverageTriangleRole.Core : CoverageTriangleRole.Collar);
        var surfaces = patch.SelectMany(host.SurfaceCorners).Distinct().Order().ToArray();
        var strengths = surfaces.Select(s => new CoverageSurfaceStrength(s, boundary.Contains(s) ? 0f : 1f)).ToArray();
        var regionTriangles = Runs(host, patch.Order(), t => (int)roles[t])
            .Select(r => new HostCoverageRegionTriangles(r.Range, (CoverageTriangleRole)r.Key)).ToArray();
        var refinement = Runs(host, patch.Order(), _ => 0).Select(r => new HostCoverageRefinement(r.Range, settings.RefinementLevel)).ToArray();
        var map = new HostCoverageMap(hostSha, host.Topology, [new HostCoverageRegion(settings.RegionKey, regionTriangles, strengths)],
            refinement, new HostCoverageAuthoring("coverage-research-footprint-proposal", "1"));
        var command = new EquipmentCoverageCommand(1, settings.PartId, settings.RegionKey, coverageLocal,
            supportRanges.Select(r => new EquipmentSupportRange(r.Mesh, r.Primitive, r.FirstTriangle, r.Count)),
            settings.CollarWidth, settings.MinZ, settings.MaxZ, settings.Clearance, settings.MaxWorldRecession);
        var definition = new EquipmentCoverageDefinition(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(attachment)),
            HostCoverageMapJson.SemanticSha256(map), [command]);
        int core = roles.Values.Count(r => r == CoverageTriangleRole.Core);
        // Host resolution against the footprint: outward triangles wholly inside the domain, and the best inset any reaches.
        int inside = 0;
        double inset = double.NegativeInfinity;
        for (int t = 0; t < host.Triangles.Length; t++)
        {
            var tri = host.Triangles[t];
            Vector3 a = q[tri.A], b = q[tri.B], c = q[tri.C];
            if (Vector3.Cross(b - a, c - a).Z <= 0) continue;
            double worst = host.Corners(t).Min(v => domain.Margin(q[v].X, q[v].Y));
            inset = Math.Max(inset, worst);
            if (worst >= 0) inside++;
        }
        return (map, definition, new CoverageResearchProposalReport(support.InwardTriangles.Count, supportRanges.Length, patch.Count, core,
            patch.Count - core, strengths.Count(s => s.Strength == 0), host.SurfaceCount, host.Positions.Length - host.SurfaceCount,
            settings.ReferenceAnchor, inside, inset));
    }

    internal static CoverageTriangleRange[] SupportRanges(CoverageSourceTopology part, int mesh, Matrix4x4 coverageLocal, double cosine)
    {
        if (!Matrix4x4.Invert(coverageLocal, out Matrix4x4 inverse)) throw new InvalidDataException("Singular coverage frame.");
        var selected = new List<CoverageSourceTriangle>();
        foreach (var t in part.Triangles)
        {
            if (t.Mesh != mesh) continue;
            Vector3 a = Vector3.Transform(part.Positions[t.A], inverse), b = Vector3.Transform(part.Positions[t.B], inverse),
                c = Vector3.Transform(part.Positions[t.C], inverse);
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared() > 0 && -Vector3.Normalize(n).Z >= cosine) selected.Add(t);
        }
        if (selected.Count == 0) throw new InvalidDataException("No inward support triangle in the selected mesh.");
        var ranges = new List<CoverageTriangleRange>();
        foreach (var t in selected.OrderBy(t => (t.Mesh, t.Primitive, t.Triangle)))
        {
            if (ranges.Count > 0 && ranges[^1] is var last && last.Mesh == t.Mesh && last.Primitive == t.Primitive
                && last.FirstTriangle + last.Count == t.Triangle)
                ranges[^1] = last with { Count = last.Count + 1 };
            else ranges.Add(new(t.Mesh, t.Primitive, t.Triangle, 1));
        }
        return ranges.ToArray();
    }

    static Matrix4x4[] Skinning(Matrix4x4[] inverseBind, Matrix4x4[] palette, Matrix4x4 model)
    {
        if (inverseBind.Length != palette.Length) throw new InvalidDataException("Palette does not match the host skin.");
        return inverseBind.Select((b, i) => b * palette[i] * model).ToArray();
    }

    static IEnumerable<(CoverageTriangleRange Range, int Key)> Runs(HostGeometry host, IEnumerable<int> triangles, Func<int, int> key)
    {
        (CoverageTriangleRange Range, int Key)? run = null;
        foreach (int t in triangles)
        {
            var o = host.Triangles[t];
            if (run is { } r && r.Range.Mesh == o.Mesh && r.Range.Primitive == o.Primitive
                && r.Range.FirstTriangle + r.Range.Count == o.Ordinal && r.Key == key(t))
                run = (r.Range with { Count = r.Range.Count + 1 }, r.Key);
            else
            {
                if (run is { } done) yield return done;
                run = (new CoverageTriangleRange(o.Mesh, o.Primitive, o.Ordinal, 1), key(t));
            }
        }
        if (run is { } last) yield return last;
    }
}

/// <summary>The support's projected convex domain in coverage XY, from its actual boundary loop.</summary>
internal sealed class CoverageResearchDomain
{
    readonly Vector2[] loop;
    readonly double sign;

    internal CoverageResearchDomain(CoverageSupportSurface support)
    {
        loop = support.BoundarySourceVertices.ToArray().Select(v => new Vector2((float)support.PrecisePositions[v].X,
            (float)support.PrecisePositions[v].Y)).ToArray();
        double area = 0;
        for (int i = 0; i < loop.Length; i++) area += Cross(loop[i], loop[(i + 1) % loop.Length]);
        sign = Math.Sign(area);
        Centroid = new Vector2(loop.Average(p => p.X), loop.Average(p => p.Y));
    }

    internal Vector2 Centroid { get; }

    static double Cross(Vector2 a, Vector2 b) => (double)a.X * b.Y - (double)a.Y * b.X;

    /// <summary>Euclidean distance to the domain, zero inside.</summary>
    internal double Distance(double x, double y) => Math.Max(0, -Signed(x, y));

    /// <summary>Signed margin: positive inside, the distance to the nearest edge line, negative outside.</summary>
    internal double Margin(double x, double y) => Signed(x, y);

    /// <summary>Inside with at least the given margin from every edge.</summary>
    internal bool Inside(double x, double y, double margin) => Signed(x, y) >= margin;

    // Positive inside: the smallest signed distance to an edge line. Outside: minus the distance to the polygon.
    double Signed(double x, double y)
    {
        double inside = double.PositiveInfinity, outside = double.PositiveInfinity;
        bool isInside = true;
        for (int i = 0; i < loop.Length; i++)
        {
            Vector2 a = loop[i], b = loop[(i + 1) % loop.Length];
            double ex = b.X - a.X, ey = b.Y - a.Y, length = Math.Sqrt(ex * ex + ey * ey);
            double side = sign * (ex * (y - a.Y) - ey * (x - a.X)) / length;
            if (side < 0) isInside = false;
            inside = Math.Min(inside, side);
            double t = Math.Clamp(((x - a.X) * ex + (y - a.Y) * ey) / (length * length), 0, 1);
            double dx = a.X + t * ex - x, dy = a.Y + t * ey - y;
            outside = Math.Min(outside, Math.Sqrt(dx * dx + dy * dy));
        }
        return isInside ? inside : -outside;
    }
}

/// <summary>Host source triangles and vertices in coverage importer ordinals, with welded surface IDs.</summary>
internal sealed class HostGeometry
{
    internal readonly record struct Triangle(int Mesh, int Primitive, int Ordinal, int A, int B, int C);

    readonly int[] surface;
    readonly Vector4[] joints, weights;
    Dictionary<int, List<int>>? adjacency;

    HostGeometry(Vector3[] positions, Vector4[] joints, Vector4[] weights, int[] surface, Triangle[] triangles,
        CoverageTopologyEntry[] topology, int surfaces)
    {
        Positions = positions; this.joints = joints; this.weights = weights; this.surface = surface; Triangles = triangles;
        Topology = topology; SurfaceCount = surfaces;
    }

    internal Vector3[] Positions { get; }
    internal Triangle[] Triangles { get; }
    internal CoverageTopologyEntry[] Topology { get; }
    internal int SurfaceCount { get; }

    internal static HostGeometry Read(byte[] glb)
    {
        using var stream = new MemoryStream(glb, writable: false);
        var root = SharpGLTF.Schema2.ModelRoot.ReadGLB(stream);
        var positions = new List<Vector3>();
        var joints = new List<Vector4>();
        var weights = new List<Vector4>();
        var topology = new List<CoverageTopologyEntry>();
        var surface = new List<int>();
        var triangles = new List<Triangle>();
        var welds = new Dictionary<(Vector3, string), int>();
        foreach (var mesh in root.LogicalMeshes)
            foreach (var (primitive, ordinal) in mesh.Primitives.Select((p, i) => (p, i)))
            {
                var pos = primitive.GetVertexAccessor("POSITION")?.AsVector3Array();
                var j = primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
                var w = primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
                if (pos == null || j == null || w == null) continue;
                int start = positions.Count;
                for (int i = 0; i < pos.Count; i++)
                {
                    var key = (pos[i], Canonical(j[i], w[i]));
                    if (!welds.TryGetValue(key, out int id)) welds.Add(key, id = welds.Count);
                    positions.Add(pos[i]); joints.Add(j[i]); weights.Add(w[i]); surface.Add(id);
                    topology.Add(new(mesh.LogicalIndex, ordinal, i, id));
                }
                int t = 0;
                foreach (var (a, b, c) in primitive.GetTriangleIndices())
                    triangles.Add(new(mesh.LogicalIndex, ordinal, t++, start + a, start + b, start + c));
            }
        return new(positions.ToArray(), joints.ToArray(), weights.ToArray(), surface.ToArray(), triangles.ToArray(),
            topology.ToArray(), welds.Count);
    }

    // Normalized influences by joint, as the importer's weld rule compares them, rendered as an exact key.
    static string Canonical(Vector4 j, Vector4 w)
    {
        var sums = new SortedDictionary<int, double>();
        for (int k = 0; k < 4; k++)
            if (w[k] != 0) sums[(int)j[k]] = sums.GetValueOrDefault((int)j[k]) + w[k];
        double total = sums.Values.Sum();
        return string.Join(';', sums.Select(p => $"{p.Key}:{BitConverter.DoubleToInt64Bits(p.Value / total)}"));
    }

    internal Vector3 Skin(int vertex, Vector3 position, Matrix4x4[] skin)
    {
        Vector4 w = weights[vertex];
        float total = w.X + w.Y + w.Z + w.W;
        Vector3 result = Vector3.Zero;
        for (int k = 0; k < 4; k++)
            if (w[k] != 0) result += Vector3.Transform(position, skin[(int)joints[vertex][k]]) * (w[k] / total);
        return result;
    }

    internal IEnumerable<int> Corners(int t) => [Triangles[t].A, Triangles[t].B, Triangles[t].C];
    internal IEnumerable<int> SurfaceCorners(int t) => Corners(t).Select(v => surface[v]);
    internal IEnumerable<(int, int)> Edges(int t)
    {
        int a = surface[Triangles[t].A], b = surface[Triangles[t].B], c = surface[Triangles[t].C];
        return [(a, b), (b, c), (c, a)];
    }

    /// <summary>The eligible triangles edge-connected to the seed through welded surface edges.</summary>
    internal HashSet<int> Component(int seed, bool[] eligible)
    {
        if (adjacency is null)
        {
            adjacency = new();
            var byEdge = new Dictionary<(int, int), List<int>>();
            for (int t = 0; t < Triangles.Length; t++)
                foreach (var (a, b) in Edges(t))
                {
                    var key = (Math.Min(a, b), Math.Max(a, b));
                    if (!byEdge.TryGetValue(key, out var list)) byEdge.Add(key, list = new());
                    list.Add(t);
                }
            foreach (var list in byEdge.Values)
                foreach (int t in list)
                {
                    if (!adjacency.TryGetValue(t, out var n)) adjacency.Add(t, n = new());
                    n.AddRange(list.Where(u => u != t));
                }
        }
        var reached = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(seed);
        while (pending.TryPop(out int t))
            if (eligible[t] && reached.Add(t))
                foreach (int u in adjacency.GetValueOrDefault(t) ?? []) pending.Push(u);
        return reached;
    }
}
