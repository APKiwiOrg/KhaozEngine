using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KhaozEngine.Attachments;
using KhaozEngine.Render3D;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Synthetic public cost fixtures. One welded 50 x 20 cell host grid of exactly 2,000 source triangles at character
/// scale (2.5 x 1 world units), split into
/// two material slices at column 25 with a real shading seam, skinned by two bones whose weights ramp across x.
/// Every region is a rectangle of cells wholly inside one slice, with a zero-strength boundary, a collar of boundary
/// cells and a full-strength core holding one raised vertex, all of whose neighbours are core, that must recede under
/// its plate.
/// Typical has two 10 x 5 cell regions (100 triangles each) refined at level 2. Limit has four 5 x 5 cell regions
/// (50 triangles each) refined at level 4. Only cells clear of a region's boundary ring are refined, so protected
/// neighbours take conforming splits. One rigid equipment part carries a plate per region, attached to the root,
/// with a four-triangle inward support sheet each. Fixed caps, clearance and recession limits are the engine's own.
/// No vertex has a fractional strength, so these fixtures never exercise fractional collar evaluation. The existing
/// coverage fixtures cover that.
/// </summary>
internal sealed class CoverageCostGeometry
{
    internal const int Columns = 50, Rows = 20, SeamColumn = 25;
    // Character scale: the grid is 2.5 x 1 world units. Command clearance, recession, collar and Z limits keep the
    // values of the existing coverage fixtures, so scale changes nothing the engine certifies against.
    internal const float Spacing = .05f, Floor = -.05f, Raised = .12f;
    internal const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    internal readonly record struct Region(string Key, int Column, int Row, int Width, int Height, int RaisedX, int RaisedY)
    {
        internal Vector2 Center => new((Column + Width / 2f - SeamColumn) * Spacing, (Row + Height / 2f - Rows / 2f) * Spacing);
        internal bool Contains(int column, int row) => column >= Column && column < Column + Width && row >= Row && row < Row + Height;
        // Boundary vertices are zero and every interior vertex is core, so the boundary cells form the collar. A
        // fractional ring would leave refined points beside the raised vertex below full strength, and a partial
        // recession there cannot clear the sheet (EquipmentIntersection), which the five-row regions cannot avoid.
        internal float Strength(int column, int row)
        {
            int i = column - Column, j = row - Row;
            return i <= 0 || j <= 0 || i >= Width || j >= Height ? 0 : 1;
        }
        internal bool Refined(int column, int row) =>
            column > Column && column < Column + Width - 1 && row > Row && row < Row + Height - 1;
    }

    // The plate shape of the existing coverage fixtures at character scale: an inward pyramid sheet whose apex sits
    // over the raised vertex, below a solid slab.
    static readonly Vector3[] Plate = [new(-.25f, -.25f, .04f), new(.25f, -.25f, .04f), new(.25f, .25f, .04f), new(-.25f, .25f, .04f),
        new(0, 0, .02f), new(-.25f, -.25f, .1f), new(.25f, -.25f, .1f), new(.25f, .25f, .1f), new(-.25f, .25f, .1f)];
    static readonly int[] PlateIndices = [0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4,
        5, 7, 6, 5, 8, 7, 0, 5, 6, 0, 6, 1, 1, 6, 7, 1, 7, 2, 2, 7, 8, 2, 8, 3, 3, 8, 5, 3, 5, 0];

    // Semantic fake hosts consume the same retained plate data without importing EquipmentGlb.
    internal static ReadOnlySpan<Vector3> EquipmentPlatePositions => Plate;
    internal static ReadOnlySpan<int> EquipmentPlateIndices => PlateIndices;

    CoverageCostGeometry(CoverageCostFixtureMode mode, int level, Region[] regions)
    {
        Mode = mode; Level = level; Regions = regions;
        (Source, Map) = BuildHost(regions, level);
        Refinement = CoverageRefinement.Build(Source, Map);
        Commands = regions.Select((r, k) => new EquipmentCoverageCommand((uint)(k + 1), 1, r.Key,
            Matrix4x4.CreateTranslation(r.Center.X, r.Center.Y, 0), [new(0, 0, 4 * k, 4)], .4f, -2, 2, .02f, .8f)).ToArray();
        Topology = Observe(Refinement, Commands.Length);
    }

    internal CoverageCostFixtureMode Mode { get; }
    internal int Level { get; }
    internal Region[] Regions { get; }
    internal CoverageSourceTopology Source { get; }
    internal HostCoverageMap Map { get; }
    /// <summary>The compiled topology, built by the same public construction the scene's conformer uses.</summary>
    internal CoverageRefinement Refinement { get; }
    internal EquipmentCoverageCommand[] Commands { get; }
    internal CoverageCostTopology Topology { get; }

    internal static CoverageCostGeometry Build(CoverageCostFixtureMode mode) => mode switch
    {
        CoverageCostFixtureMode.Typical => new(mode, 2, [new("r0", 5, 7, 10, 5, 5, 2), new("r1", 30, 7, 10, 5, 5, 2)]),
        CoverageCostFixtureMode.Limit => new(mode, 4, [new("r0", 3, 7, 5, 5, 2, 2), new("r1", 15, 7, 5, 5, 2, 2),
            new("r2", 30, 7, 5, 5, 2, 2), new("r3", 42, 7, 5, 5, 2, 2)]),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static CoverageCostTopology Observe(CoverageRefinement topology, int commands) => new(
        topology.SourceVertexCount, topology.Source.Triangles.Count, (int)topology.Map.Regions.Sum(r => r.TriangleCount),
        topology.Map.Refinement.Sum(r => r.Triangles.Count), topology.VertexCount, topology.TriangleCount, commands,
        topology.Source.Materials.Count,
        string.Join(',', topology.Map.Refinement.Select(r => r.Level).Distinct().Order()
            .Select(l => l.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        topology.Map.Regions.Count, Fingerprint(topology));

    // SHA-256 over the map's semantic hash, the source positions, the refined indices and every refined vertex record.
    static string Fingerprint(CoverageRefinement topology)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(HostCoverageMapJson.SemanticSha256(topology.Map)));
        hash.AppendData(MemoryMarshal.AsBytes(topology.Source.Positions));
        hash.AppendData(MemoryMarshal.AsBytes(topology.Indices));
        Span<byte> record = stackalloc byte[32];
        foreach (var v in topology.Vertices)
        {
            BinaryPrimitives.WriteInt32LittleEndian(record, v.A);
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], v.B);
            BinaryPrimitives.WriteInt32LittleEndian(record[8..], v.C);
            BinaryPrimitives.WriteInt32LittleEndian(record[12..], v.Denominator);
            BinaryPrimitives.WriteInt32LittleEndian(record[16..], v.PositionGroup);
            BinaryPrimitives.WriteInt32LittleEndian(record[20..], v.ShadingGroup);
            BinaryPrimitives.WriteInt32LittleEndian(record[24..], v.RegionIndex);
            BinaryPrimitives.WriteSingleLittleEndian(record[28..], v.Strength);
            hash.AppendData(record);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static Skeleton CreateSkeleton() => new([-1, 0], [JointPose.Identity, JointPose.Identity], [0, 1], [0, 1], ["root", "hip"]);

    /// <summary>Lifted moves both joints together. Bent lifts and shifts the hip alone, so the host deforms
    /// relative to its root-attached plates and the covered surface changes between frames.</summary>
    internal static void Pose(int frame, Span<Matrix4x4> palette)
    {
        if (frame % 2 == 0) palette.Fill(Matrix4x4.CreateTranslation(0, .1f, 0));
        else { palette[0] = Matrix4x4.Identity; palette[1] = Matrix4x4.CreateTranslation(.02f, 0, .02f); }
    }

    /// <summary>One covered skinned mesh per material slice, in slice vertex order.</summary>
    internal SkinnedGltfMesh[] HostMeshes(Skeleton skeleton) => Source.Materials.Select(material =>
    {
        var vertices = Source.Vertices.Select((v, i) => (v, i)).Where(p => p.v.MaterialSlice == material.Slice)
            .OrderBy(p => p.v.SliceVertex).Select(p => Source.SkinVertices[p.i]).ToArray();
        var indices = Source.Triangles.Where(t => t.MaterialSlice == material.Slice).SelectMany(t =>
            new[] { (uint)Source.Vertices[t.A].SliceVertex, (uint)Source.Vertices[t.B].SliceVertex, (uint)Source.Vertices[t.C].SliceVertex })
            .ToArray();
        var identity = new[] { Matrix4x4.Identity, Matrix4x4.Identity };
        var mesh = new SkinnedGltfMesh(vertices, indices, identity, (Matrix4x4[])identity.Clone(), skeleton);
        return new SkinnedGltfMesh(vertices, indices, mesh.InverseBind, mesh.RestPose, skeleton)
        { Coverage = HostCoveragePart.Create(Map, Source, material.Slice, mesh) };
    }).ToArray();

    /// <summary>The equipment GLB. Inward sheets come first in primitive 0, plate k owning triangles 4k to 4k+3.</summary>
    internal byte[] EquipmentGlb()
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty>("plates");
        var inward = mesh.UsePrimitive(new MaterialBuilder("inward"));
        var solid = mesh.UsePrimitive(new MaterialBuilder("solid"));
        foreach (var region in Regions)
            for (int t = 0; t < PlateIndices.Length / 3; t++)
            {
                VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> V(int c) => new(new VertexPositionNormal(
                    Plate[PlateIndices[t * 3 + c]] + new Vector3(region.Center, 0), Vector3.UnitZ));
                (t < 4 ? inward : solid).AddTriangle(V(0), V(1), V(2));
            }
        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, Matrix4x4.Identity);
        string path = Path.Combine(Path.GetTempPath(), $"coverage-cost-{Guid.NewGuid():N}.glb");
        try { scene.ToGltf2().SaveGLB(path); return File.ReadAllBytes(path); }
        finally { File.Delete(path); }
    }

    static (CoverageSourceTopology, HostCoverageMap) BuildHost(Region[] regions, int level)
    {
        var positions = new List<Vector3>();
        var skin = new List<SkinnedVertex>();
        var vertices = new List<CoverageSourceVertex>();
        var topology = new List<CoverageTopologyEntry>();
        var triangles = new List<CoverageSourceTriangle>();
        var roles = regions.Select(_ => new List<HostCoverageRegionTriangles>()).ToArray();
        var strengths = regions.Select(_ => new List<CoverageSurfaceStrength>()).ToArray();
        var refinements = new List<HostCoverageRefinement>();
        for (int slice = 0; slice < 2; slice++)
        {
            int first = slice * SeamColumn, width = SeamColumn + 1, start = positions.Count;
            for (int row = 0; row <= Rows; row++)
                for (int column = first; column <= first + SeamColumn; column++)
                {
                    int surface = row * (Columns + 1) + column;
                    bool raised = regions.Any(r => column == r.Column + r.RaisedX && row == r.Row + r.RaisedY);
                    var p = new Vector3((column - SeamColumn) * Spacing, (row - Rows / 2f) * Spacing, raised ? Raised : Floor);
                    float weight = column / (float)Columns;
                    int local = positions.Count - start;
                    positions.Add(p);
                    skin.Add(new SkinnedVertex
                    {
                        Position = p,
                        Normal = slice == 1 ? Vector3.Normalize(new(.3f, 0, 1)) : Vector3.UnitZ,
                        Color = new(1, .5f, .25f, 1),
                        BoneIndices = new(0, 1, 0, 0),
                        BoneWeights = new(1 - weight, weight, 0, 0),
                        Tangent = new(1, 0, 0, -1),
                    });
                    vertices.Add(new(0, slice, local, -1, slice, local));
                    topology.Add(new(0, slice, local, surface));
                    for (int k = 0; k < regions.Length; k++)
                        if (column >= regions[k].Column && column <= regions[k].Column + regions[k].Width
                            && row >= regions[k].Row && row <= regions[k].Row + regions[k].Height)
                            strengths[k].Add(new(surface, regions[k].Strength(column, row)));
                }
            int ordinal = 0;
            for (int row = 0; row < Rows; row++)
                for (int column = first; column < first + SeamColumn; column++)
                {
                    int a = start + row * width + column - first, b = a + 1, c = a + width, d = c + 1;
                    int owner = Array.FindIndex(regions, r => r.Contains(column, row));
                    foreach (var (i, j, k) in new[] { (a, b, d), (a, d, c) })
                    {
                        triangles.Add(new(0, slice, ordinal, -1, slice, i, j, k));
                        if (owner >= 0)
                        {
                            var range = new CoverageTriangleRange(0, slice, ordinal, 1);
                            var r = regions[owner];
                            bool core = new[] { i, j, k }.All(v => r.Strength(Column(v), Row(v)) == 1);
                            roles[owner].Add(new(range, core ? CoverageTriangleRole.Core : CoverageTriangleRole.Collar));
                            if (r.Refined(column, row)) refinements.Add(new(range, level));
                        }
                        ordinal++;
                    }
                }
            int Column(int vertex) => first + (vertex - start) % width;
            int Row(int vertex) => (vertex - start) / width;
        }
        var source = new CoverageSourceTopology("synthetic-cost-host", Hash, positions.ToArray(), vertices, triangles, skin.ToArray());
        var map = new HostCoverageMap(Hash, topology, regions.Select((r, k) => new HostCoverageRegion(r.Key, roles[k], strengths[k])),
            refinements, new("synthetic", "cost-fixture"));
        return (source, map);
    }
}
