using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>One decoded vertex and its source and material-slice ordinals. Node is -1 for unplaced or skinned data.</summary>
public readonly record struct CoverageSourceVertex(int Mesh, int Primitive, int Vertex, int Node, int MaterialSlice, int SliceVertex);

/// <summary>One displayed source triangle. A, B and C index the shared source vertex array in original winding.</summary>
public readonly record struct CoverageSourceTriangle(int Mesh, int Primitive, int Triangle, int Node, int MaterialSlice, int A, int B, int C);

/// <summary>Raw source influences, canonicalized by joint for exact seam validation before decoder rounding.</summary>
public readonly record struct CoverageSkinInfluence(Vector4 Joints, Vector4 Weights);

/// <summary>Placement already baked by rigid import. Skinned import uses one identity occurrence per mesh.</summary>
public readonly record struct CoverageSourceOccurrence(int Mesh, int Node, Matrix4x4 Transform);

/// <summary>Original material identity. AlphaMode zero means opaque, nonzero requires unsupported cutout or blending.</summary>
public readonly record struct CoverageMaterialSlice(int Slice, int SourceMaterial, int AlphaMode);

/// <summary>Owned decoded geometry before coverage refinement. Programmatic sources use the same explicit provenance.</summary>
public sealed class CoverageSourceTopology
{
    readonly Vector3[] positions;
    readonly SkinnedVertex[] skinVertices;
    readonly CoverageSkinInfluence[] influences;

    public CoverageSourceTopology(string identity, string sourceSha256, ReadOnlySpan<Vector3> positions,
        IEnumerable<CoverageSourceVertex> vertices, IEnumerable<CoverageSourceTriangle> triangles,
        ReadOnlySpan<SkinnedVertex> skinVertices = default, ReadOnlySpan<CoverageSkinInfluence> influences = default,
        IEnumerable<CoverageSourceOccurrence>? occurrences = null, IEnumerable<CoverageMaterialSlice>? materials = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(triangles);
        Identity = identity;
        SourceSha256 = sourceSha256;
        if (skinVertices.Length != 0 && skinVertices.Length != positions.Length)
            throw Error(CoverageGeometryRefusal.SkinArrayLength, "Source skin arrays have different lengths.", count: skinVertices.Length, limit: positions.Length);
        if (!influences.IsEmpty && influences.Length != skinVertices.Length)
            throw Error(CoverageGeometryRefusal.InfluenceArrayLength, "Source influence arrays have different lengths.", count: influences.Length, limit: skinVertices.Length);
        this.positions = positions.ToArray();
        this.skinVertices = skinVertices.ToArray();
        this.influences = influences.IsEmpty ? this.skinVertices.Select(v => new CoverageSkinInfluence(v.BoneIndices, v.BoneWeights)).ToArray() : influences.ToArray();
        int vertexCount = positions.Length;
        var ownedVertices = BoundedCollections.Snapshot(vertices, vertexCount, count =>
            Error(CoverageGeometryRefusal.VertexArrayLength, "Source vertex arrays have different lengths.", count: count, limit: vertexCount));
        var ownedTriangles = triangles.ToArray();
        if (ownedVertices.Length == 0 || ownedTriangles.Length == 0) throw Error(CoverageGeometryRefusal.EmptySource, "Source has no displayed geometry.");
        if (ownedVertices.Length != positions.Length)
            throw Error(CoverageGeometryRefusal.VertexArrayLength, "Source vertex arrays have different lengths.", count: ownedVertices.Length, limit: positions.Length);
        for (int i = 0; i < positions.Length; i++)
        {
            Vector3 position = positions[i];
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw Error(CoverageGeometryRefusal.NonfinitePosition, "Source positions must be finite.", vertex: i);
        }
        var seen = new HashSet<(int, int, int, int)>();
        for (int i = 0; i < ownedVertices.Length; i++)
        {
            CoverageSourceVertex vertex = ownedVertices[i];
            if (vertex.Mesh < 0 || vertex.Primitive < 0 || vertex.Vertex < 0 || vertex.Node < -1 || vertex.MaterialSlice < 0 || vertex.SliceVertex < 0)
                throw Error(CoverageGeometryRefusal.InvalidVertexProvenance, "Invalid source vertex provenance.", mesh: vertex.Mesh, primitive: vertex.Primitive, vertex: i, node: vertex.Node, materialSlice: vertex.MaterialSlice);
        }
        for (int i = 0; i < ownedTriangles.Length; i++)
        {
            CoverageSourceTriangle triangle = ownedTriangles[i];
            if (triangle.Mesh < 0 || triangle.Primitive < 0 || triangle.Triangle < 0 || triangle.Node < -1 || triangle.MaterialSlice < 0)
                throw Error(CoverageGeometryRefusal.InvalidTriangleProvenance, "Invalid source triangle provenance.", triangle: i);
            if (!seen.Add((triangle.Mesh, triangle.Primitive, triangle.Triangle, triangle.Node)))
                throw Error(CoverageGeometryRefusal.DuplicateTriangle, "Repeated source triangle provenance.", mesh: triangle.Mesh, primitive: triangle.Primitive, triangle: i, node: triangle.Node);
            foreach (int corner in new[] { triangle.A, triangle.B, triangle.C })
            {
                if ((uint)corner >= (uint)positions.Length) throw Error(CoverageGeometryRefusal.TriangleIndex, "Source triangle index is out of range.", vertex: corner, triangle: i);
                CoverageSourceVertex vertex = ownedVertices[corner];
                if (vertex.Mesh != triangle.Mesh || vertex.Primitive != triangle.Primitive || vertex.Node != triangle.Node || vertex.MaterialSlice != triangle.MaterialSlice)
                    throw Error(CoverageGeometryRefusal.TriangleOwnership, "Source triangle does not own its vertices.", triangle: i);
            }
        }
        if (this.influences.Length != this.skinVertices.Length) throw Error(CoverageGeometryRefusal.InfluenceArrayLength, "Source influence arrays have different lengths.", count: this.influences.Length, limit: this.skinVertices.Length);
        for (int i = 0; i < this.skinVertices.Length; i++)
        {
            if (this.skinVertices[i].Position != this.positions[i]) throw Error(CoverageGeometryRefusal.SkinPositionMismatch, "Skin and source positions disagree.", vertex: i);
            if (!influences.IsEmpty && (this.skinVertices[i].BoneIndices != this.influences[i].Joints
                || this.skinVertices[i].BoneWeights != SkinningMath.NormalizeWeights(this.influences[i].Weights)))
                throw Error(CoverageGeometryRefusal.SkinInfluenceMismatch, "Raw source influences disagree with decoded skin data.", vertex: i);
        }
        var ownedOccurrences = (occurrences ?? ownedTriangles.Select(t => new CoverageSourceOccurrence(t.Mesh, t.Node, Matrix4x4.Identity)).Distinct()).ToArray();
        var ownedMaterials = (materials ?? ownedTriangles.Select(t => new CoverageMaterialSlice(t.MaterialSlice, -1, 0)).Distinct()).ToArray();
        var occurrenceKeys = new HashSet<(int, int)>();
        foreach (var occurrence in ownedOccurrences)
            if (!occurrenceKeys.Add((occurrence.Mesh, occurrence.Node)))
                throw Error(CoverageGeometryRefusal.DuplicateOccurrence, "Repeated occurrence identity.", mesh: occurrence.Mesh, node: occurrence.Node);
        var materialKeys = new HashSet<int>();
        foreach (var material in ownedMaterials)
            if (!materialKeys.Add(material.Slice)) throw Error(CoverageGeometryRefusal.DuplicateMaterial, "Repeated material identity.", materialSlice: material.Slice);
        foreach (CoverageSourceTriangle triangle in ownedTriangles)
        {
            if (!occurrenceKeys.Contains((triangle.Mesh, triangle.Node)))
                throw Error(CoverageGeometryRefusal.MissingOccurrence, "Source triangle has no occurrence identity.", mesh: triangle.Mesh, primitive: triangle.Primitive, node: triangle.Node);
            if (!materialKeys.Contains(triangle.MaterialSlice))
                throw Error(CoverageGeometryRefusal.MissingMaterial, "Source triangle has no material identity.", materialSlice: triangle.MaterialSlice);
        }
        Occurrences = Array.AsReadOnly(ownedOccurrences);
        Materials = Array.AsReadOnly(ownedMaterials);
        Vertices = Array.AsReadOnly(ownedVertices);
        Triangles = Array.AsReadOnly(ownedTriangles);
    }

    public string Identity { get; }
    public string SourceSha256 { get; }
    public ReadOnlySpan<Vector3> Positions => positions;
    public ReadOnlySpan<SkinnedVertex> SkinVertices => skinVertices;
    public ReadOnlySpan<CoverageSkinInfluence> Influences => influences;
    public IReadOnlyList<CoverageSourceOccurrence> Occurrences { get; }
    public IReadOnlyList<CoverageMaterialSlice> Materials { get; }
    public IReadOnlyList<CoverageSourceVertex> Vertices { get; }
    public IReadOnlyList<CoverageSourceTriangle> Triangles { get; }
    internal CoverageGeometryDataException Error(CoverageGeometryRefusal reason, string message,
        int? mesh = null, int? primitive = null, int? vertex = null, int? triangle = null, int? node = null, int? materialSlice = null,
        long? count = null, long? limit = null) => new(reason, $"Coverage asset '{Identity}': {message}")
    { Identity = Identity, Mesh = mesh, Primitive = primitive, Vertex = vertex, Triangle = triangle, Node = node, MaterialSlice = materialSlice, Count = count, Limit = limit };

    internal HostCoverageDataException Error(HostCoverageRefusal reason, string message, int? mesh = null,
        int? primitive = null, int? vertex = null, int? firstTriangle = null, int? materialSlice = null,
        int? surfaceId = null, string? regionKey = null) =>
        new(reason, $"Coverage asset '{Identity}': {message}")
        { Identity = Identity, Mesh = mesh, Primitive = primitive, Vertex = vertex, FirstTriangle = firstTriangle,
            MaterialSlice = materialSlice, SurfaceId = surfaceId, RegionKey = regionKey };

    internal int[] Select(ReadOnlySpan<CoverageTriangleRange> ranges)
    {
        var selected = new HashSet<int>();
        foreach (CoverageTriangleRange range in ranges)
        {
            if (range.Mesh < 0 || range.Primitive < 0 || range.FirstTriangle < 0 || range.Count < 1
                || (long)range.FirstTriangle + range.Count > int.MaxValue) throw Error(CoverageGeometryRefusal.InvalidTriangleRange, "Invalid source triangle range.", mesh: range.Mesh, primitive: range.Primitive, triangle: range.FirstTriangle);
            var ordinals = new HashSet<int>();
            for (int i = 0; i < Triangles.Count; i++)
            {
                CoverageSourceTriangle triangle = Triangles[i];
                if (triangle.Mesh != range.Mesh || triangle.Primitive != range.Primitive
                    || triangle.Triangle < range.FirstTriangle || (long)triangle.Triangle >= (long)range.FirstTriangle + range.Count) continue;
                if (!selected.Add(i)) throw Error(CoverageGeometryRefusal.TriangleRangeOverlap, "Source triangle ranges overlap.", mesh: range.Mesh, primitive: range.Primitive, triangle: range.FirstTriangle);
                ordinals.Add(triangle.Triangle);
            }
            if (ordinals.Count != range.Count) throw Error(CoverageGeometryRefusal.TriangleRangeMissing, "Source triangle range is not present in the displayed geometry.", mesh: range.Mesh, primitive: range.Primitive, triangle: range.FirstTriangle);
        }
        return selected.Order().ToArray();
    }
}
