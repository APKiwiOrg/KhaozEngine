using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace KhaozEngine.Render3D;

/// <summary>Validated host metadata and the exact decoded material slice it accompanies.</summary>
public sealed class HostCoveragePart
{
    readonly string fingerprint;
    HostCoveragePart(HostCoverageMap map, CoverageSourceTopology source, int materialSlice, SkinnedGltfMesh mesh)
    {
        Map = map;
        Source = source;
        MaterialSlice = materialSlice;
        SourceTriangles = Array.AsReadOnly(source.Triangles.Where(t => t.MaterialSlice == materialSlice).ToArray());
        fingerprint = Fingerprint(mesh);
    }

    public HostCoverageMap Map { get; }
    public CoverageSourceTopology Source { get; }
    public int MaterialSlice { get; }
    public IReadOnlyList<CoverageSourceTriangle> SourceTriangles { get; }
    public string MeshFingerprint => fingerprint;
    public bool Matches(SkinnedGltfMesh mesh) => fingerprint == Fingerprint(mesh);

    /// <summary>Validate explicit programmatic host topology and bind one material slice to its decoded mesh.</summary>
    public static HostCoveragePart Create(HostCoverageMap map, CoverageSourceTopology source, int materialSlice, SkinnedGltfMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mesh);
        Validate(map, source);
        return CreateSlice(map, source, materialSlice, mesh);
    }

    internal static HostCoveragePart CreateSlice(HostCoverageMap map, CoverageSourceTopology source, int materialSlice, SkinnedGltfMesh mesh)
    {
        var triangles = source.Triangles.Where(t => t.MaterialSlice == materialSlice).ToArray();
        var vertices = source.Vertices.Select((v, i) => (v, i)).Where(pair => pair.v.MaterialSlice == materialSlice).ToArray();
        if (vertices.Length == 0 || triangles.Length == 0 || vertices.Length != mesh.Vertices.Length || triangles.Length * 3 != mesh.Indices32.Length)
            throw source.Error("Material slice does not match the mesh.");
        foreach (var (vertex, index) in vertices)
            if ((uint)vertex.SliceVertex >= (uint)mesh.Vertices.Length || !source.SkinVertices[index].Equals(mesh.Vertices[vertex.SliceVertex]))
                throw source.Error("Material slice vertex changed.");
        for (int i = 0; i < triangles.Length; i++)
        {
            CoverageSourceTriangle triangle = triangles[i];
            if (mesh.Indices32[i * 3] != source.Vertices[triangle.A].SliceVertex
                || mesh.Indices32[i * 3 + 1] != source.Vertices[triangle.B].SliceVertex
                || mesh.Indices32[i * 3 + 2] != source.Vertices[triangle.C].SliceVertex)
                throw source.Error("Material slice winding changed.");
        }
        if (mesh.BoneCount is < 1 or > SkinningMath.MaxBonesPerDraw
            || mesh.Vertices.Any(vertex => !SkinningMath.AreBoneIndicesValid(vertex.BoneIndices, mesh.BoneCount)))
            throw source.Error("Material slice has an unsupported skin.");
        foreach (Matrix4x4 matrix in mesh.InverseBind.Concat(mesh.RestPose))
            foreach (float value in MemoryMarshal.Cast<Matrix4x4, float>(new[] { matrix }.AsSpan()))
                if (!float.IsFinite(value)) throw source.Error("Material slice has a nonfinite skin matrix.");
        return new HostCoveragePart(map, source, materialSlice, mesh);
    }

    internal static void Validate(HostCoverageMap map, CoverageSourceTopology source)
    {
        if (map.HostSha256 != source.SourceSha256) throw source.Error("Host GLB identity mismatch.");
        if (source.SkinVertices.Length != source.Vertices.Count) throw source.Error("Host source requires skin vertices.");
        var authored = map.Topology.ToDictionary(v => (v.Mesh, v.Primitive, v.Vertex), v => v.SurfaceId);
        var ids = new int[source.Vertices.Count];
        var representatives = new Dictionary<int, int>();
        var canonical = new (int, double)[ids.Length][];
        var used = new HashSet<(int, int, int)>();
        for (int i = 0; i < ids.Length; i++)
        {
            CoverageSourceVertex vertex = source.Vertices[i];
            canonical[i] = Canonical(source.Influences[i], source);
            var key = (vertex.Mesh, vertex.Primitive, vertex.Vertex);
            if (!authored.TryGetValue(key, out int surface)) throw source.Error("Source vertex is absent from the host topology.");
            used.Add(key);
            ids[i] = surface;
            if (representatives.TryGetValue(surface, out int other))
            {
                if (source.Positions[i] != source.Positions[other] || !canonical[i].SequenceEqual(canonical[other]))
                    throw source.Error("Welded vertices disagree in rest position or normalized influences.");
            }
            else representatives.Add(surface, i);
        }
        if (used.Count != authored.Count) throw source.Error("Host topology names an absent source vertex.");
        var wholeEdges = new Dictionary<(int, int), (int Count, int Direction)>();
        var links = new Dictionary<int, List<(int, int)>>();
        var faces = new HashSet<(int, int, int)>();
        var repeatedFaces = new HashSet<int>();
        foreach (CoverageSourceTriangle triangle in source.Triangles)
        {
            int a = ids[triangle.A], b = ids[triangle.B], c = ids[triangle.C];
            int[] sorted = { a, b, c };
            Array.Sort(sorted);
            if (!faces.Add((sorted[0], sorted[1], sorted[2]))) { repeatedFaces.Add(a); repeatedFaces.Add(b); repeatedFaces.Add(c); }
            WholeEdge(a, b); WholeEdge(b, c); WholeEdge(c, a);
            Link(a, b, c); Link(b, c, a); Link(c, a, b);
        }
        foreach (HostCoverageRefinement refinement in map.Refinement) source.Select(new[] { refinement.Triangles });
        foreach (HostCoverageRegion region in map.Regions)
        {
            var strengths = region.Strengths.ToDictionary(s => s.SurfaceId, s => s.Strength);
            var edges = new Dictionary<(int, int), (int Count, int Direction)>();
            var selected = new HashSet<int>();
            var regionVertices = new HashSet<int>();
            foreach (HostCoverageRegionTriangles entry in region.Triangles)
                foreach (int index in source.Select(new[] { entry.Triangles }))
                {
                    if (!selected.Add(index)) throw source.Error("Region triangle ranges overlap.");
                    CoverageSourceTriangle triangle = source.Triangles[index];
                    int a = ids[triangle.A], b = ids[triangle.B], c = ids[triangle.C];
                    if (a == b || b == c || a == c || Vector3.Cross(source.Positions[triangle.B] - source.Positions[triangle.A],
                        source.Positions[triangle.C] - source.Positions[triangle.A]).LengthSquared() == 0)
                        throw source.Error("Affected triangle is degenerate.");
                    foreach (int id in new[] { a, b, c })
                    {
                        regionVertices.Add(id);
                        if (!strengths.TryGetValue(id, out float strength)) throw source.Error("Region vertex has no explicit strength.");
                        if (entry.Role == CoverageTriangleRole.Core && strength != 1) throw source.Error("Core strength must equal one.");
                    }
                    Edge(a, b); Edge(b, c); Edge(c, a);
                }
            foreach (var pair in edges)
            {
                if (pair.Value.Count > 2 || pair.Value.Count == 2 && pair.Value.Direction != 0)
                    throw source.Error("Affected topology is nonmanifold or has inconsistent winding.");
                if (pair.Value.Count == 1 && (strengths[pair.Key.Item1] != 0 || strengths[pair.Key.Item2] != 0))
                    throw source.Error("Region boundary strength must equal zero.");
            }
            if (strengths.Any(pair => !regionVertices.Contains(pair.Key))) throw source.Error("Strength names a vertex outside its region.");
            foreach (var pair in edges)
            {
                var whole = wholeEdges[pair.Key];
                if (whole.Count > 2 || whole.Count == 2 && whole.Direction != 0) throw source.Error("Affected host edge is nonmanifold.");
            }
            foreach (int surface in regionVertices)
            {
                if (repeatedFaces.Contains(surface)) throw source.Error("Affected host has repeated surface faces.");
                ValidateLink(links[surface], source);
            }
            void Edge(int a, int b)
            {
                var key = (Math.Min(a, b), Math.Max(a, b));
                edges.TryGetValue(key, out var value);
                edges[key] = (value.Count + 1, value.Direction + (a < b ? 1 : -1));
            }
        }
        void WholeEdge(int a, int b)
        {
            var key = (Math.Min(a, b), Math.Max(a, b));
            wholeEdges.TryGetValue(key, out var value);
            wholeEdges[key] = (value.Count + 1, value.Direction + (a < b ? 1 : -1));
        }
        void Link(int vertex, int a, int b)
        {
            if (!links.TryGetValue(vertex, out var list)) links.Add(vertex, list = new());
            list.Add((a, b));
        }
    }

    static void ValidateLink(List<(int A, int B)> links, CoverageSourceTopology source)
    {
        var graph = new Dictionary<int, List<int>>();
        foreach (var edge in links)
        {
            if (edge.A == edge.B) throw source.Error("Affected host vertex is degenerate.");
            Add(edge.A, edge.B); Add(edge.B, edge.A);
        }
        if (graph.Values.Any(neighbors => neighbors.Count > 2)
            || graph.Values.Count(neighbors => neighbors.Count == 1) is not (0 or 2))
            throw source.Error("Affected host vertex is nonmanifold.");
        var reached = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(graph.Keys.First());
        while (pending.TryPop(out int vertex))
            if (reached.Add(vertex))
                foreach (int neighbor in graph[vertex]) pending.Push(neighbor);
        if (reached.Count != graph.Count) throw source.Error("Affected host vertex has a disconnected one-ring.");
        void Add(int a, int b)
        {
            if (!graph.TryGetValue(a, out var neighbors)) graph.Add(a, neighbors = new());
            neighbors.Add(b);
        }
    }

    static (int, double)[] Canonical(CoverageSkinInfluence influence, CoverageSourceTopology source)
    {
        var weights = new SortedDictionary<int, double>();
        for (int slot = 0; slot < 4; slot++)
        {
            float joint = influence.Joints[slot], weight = influence.Weights[slot];
            if (!float.IsFinite(joint) || joint < 0 || joint >= int.MaxValue || joint != MathF.Truncate(joint) || !float.IsFinite(weight) || weight < 0)
                throw source.Error("Invalid skin influence.");
            if (weight == 0) continue;
            int id = (int)joint;
            weights.TryGetValue(id, out double sum);
            weights[id] = sum + weight;
        }
        double total = weights.Values.Sum();
        return weights.Select(pair => (pair.Key, pair.Value / total)).ToArray();
    }

    static string Fingerprint(SkinnedGltfMesh mesh)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add<T>(T[] values) where T : unmanaged
        {
            hash.AppendData(BitConverter.GetBytes(values.Length));
            hash.AppendData(MemoryMarshal.AsBytes(values.AsSpan()));
        }
        Add(mesh.Vertices); Add(mesh.Indices32); Add(mesh.InverseBind); Add(mesh.RestPose);
        if (mesh.Skeleton is { } skeleton)
        {
            Add(skeleton.ParentIndices); Add(skeleton.RestLocal); Add(skeleton.NodeLogicalIndex); Add(skeleton.JointToNode);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
