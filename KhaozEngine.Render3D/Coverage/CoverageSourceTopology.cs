using System;
using System.Collections.Generic;
using System.IO;
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
        this.positions = positions.ToArray();
        this.skinVertices = skinVertices.ToArray();
        this.influences = influences.IsEmpty ? this.skinVertices.Select(v => new CoverageSkinInfluence(v.BoneIndices, v.BoneWeights)).ToArray() : influences.ToArray();
        var ownedVertices = vertices.ToArray();
        var ownedTriangles = triangles.ToArray();
        if (ownedVertices.Length != positions.Length || (skinVertices.Length != 0 && skinVertices.Length != positions.Length))
            throw Error("Source vertex arrays have different lengths.");
        foreach (Vector3 position in positions)
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw Error("Source positions must be finite.");
        var seen = new HashSet<(int, int, int, int)>();
        foreach (CoverageSourceVertex vertex in ownedVertices)
            if (vertex.Mesh < 0 || vertex.Primitive < 0 || vertex.Vertex < 0 || vertex.Node < -1 || vertex.MaterialSlice < 0 || vertex.SliceVertex < 0)
                throw Error("Invalid source vertex provenance.");
        foreach (CoverageSourceTriangle triangle in ownedTriangles)
        {
            if (triangle.Mesh < 0 || triangle.Primitive < 0 || triangle.Triangle < 0 || triangle.Node < -1 || triangle.MaterialSlice < 0
                || !seen.Add((triangle.Mesh, triangle.Primitive, triangle.Triangle, triangle.Node)))
                throw Error("Invalid or repeated source triangle provenance.");
            foreach (int corner in new[] { triangle.A, triangle.B, triangle.C })
            {
                if ((uint)corner >= (uint)positions.Length) throw Error("Source triangle index is out of range.");
                CoverageSourceVertex vertex = ownedVertices[corner];
                if (vertex.Mesh != triangle.Mesh || vertex.Primitive != triangle.Primitive || vertex.Node != triangle.Node || vertex.MaterialSlice != triangle.MaterialSlice)
                    throw Error("Source triangle does not own its vertices.");
            }
        }
        if (this.influences.Length != this.skinVertices.Length) throw Error("Source influence arrays have different lengths.");
        for (int i = 0; i < this.skinVertices.Length; i++)
        {
            if (this.skinVertices[i].Position != this.positions[i]) throw Error("Skin and source positions disagree.");
            if (!influences.IsEmpty && (this.skinVertices[i].BoneIndices != this.influences[i].Joints
                || this.skinVertices[i].BoneWeights != SkinningMath.NormalizeWeights(this.influences[i].Weights)))
                throw Error("Raw source influences disagree with decoded skin data.");
        }
        var ownedOccurrences = (occurrences ?? ownedTriangles.Select(t => new CoverageSourceOccurrence(t.Mesh, t.Node, Matrix4x4.Identity)).Distinct()).ToArray();
        var ownedMaterials = (materials ?? ownedTriangles.Select(t => new CoverageMaterialSlice(t.MaterialSlice, -1, 0)).Distinct()).ToArray();
        if (ownedOccurrences.Select(o => (o.Mesh, o.Node)).Distinct().Count() != ownedOccurrences.Length
            || ownedMaterials.Select(m => m.Slice).Distinct().Count() != ownedMaterials.Length)
            throw Error("Repeated occurrence or material identity.");
        foreach (CoverageSourceTriangle triangle in ownedTriangles)
            if (!ownedOccurrences.Any(o => o.Mesh == triangle.Mesh && o.Node == triangle.Node)
                || !ownedMaterials.Any(m => m.Slice == triangle.MaterialSlice)) throw Error("Source triangle has no occurrence or material identity.");
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
    internal InvalidDataException Error(string message) => new($"Coverage asset '{Identity}': {message}");

    internal int[] Select(ReadOnlySpan<CoverageTriangleRange> ranges)
    {
        var selected = new HashSet<int>();
        foreach (CoverageTriangleRange range in ranges)
        {
            if (range.Mesh < 0 || range.Primitive < 0 || range.FirstTriangle < 0 || range.Count < 1
                || (long)range.FirstTriangle + range.Count > int.MaxValue) throw Error("Invalid source triangle range.");
            var ordinals = new HashSet<int>();
            for (int i = 0; i < Triangles.Count; i++)
            {
                CoverageSourceTriangle triangle = Triangles[i];
                if (triangle.Mesh != range.Mesh || triangle.Primitive != range.Primitive
                    || triangle.Triangle < range.FirstTriangle || (long)triangle.Triangle >= (long)range.FirstTriangle + range.Count) continue;
                if (!selected.Add(i)) throw Error("Source triangle ranges overlap.");
                ordinals.Add(triangle.Triangle);
            }
            if (ordinals.Count != range.Count) throw Error("Source triangle range is not present in the displayed geometry.");
        }
        return selected.Order().ToArray();
    }
}
