using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Synthetic planar cloth with indexed triangle seams, shared positions and real refinement.
/// Extents can grow for a count-only workload manifest without importing consumer geometry.</summary>
internal sealed class CoverageRecessionBodySizeFixture
{
    internal readonly CoverageSourceTopology Source;
    internal readonly HostCoverageMap Map;
    internal readonly CoverageRefinement Topology;
    internal readonly CoverageRecessionTable Table;
    internal readonly SkinnedGltfMesh Mesh;
    internal readonly Skeleton Skeleton = new([-1], [JointPose.Identity], [0], [0], ["root"]);
    internal readonly string FixtureHash, TableHash;
    internal readonly int Width, Height, Level;

    internal CoverageRecessionBodySizeFixture(int width = 18, int height = 16, int level = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);
        Width = width; Height = height; Level = level;
        var skin = new List<SkinnedVertex>();
        var provenance = new List<CoverageSourceVertex>();
        var vertices = new List<CoverageTopologyEntry>();
        var triangles = new List<CoverageSourceTriangle>();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                Face((x, y), (x + 1, y), (x + 1, y + 1));
                Face((x, y), (x + 1, y + 1), (x, y + 1));
            }
        var sourceVertices = skin.ToArray();
        uint[] indices = Enumerable.Range(0, sourceVertices.Length).Select(i => (uint)i).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MemoryMarshal.AsBytes(sourceVertices.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(indices.AsSpan()));
        hash.AppendData(BitConverter.GetBytes(level));
        FixtureHash = Convert.ToHexStringLower(hash.GetHashAndReset());
        Source = new("synthetic-recession-grid", FixtureHash, sourceVertices.Select(v => v.Position).ToArray(),
            provenance, triangles, sourceVertices);
        var strengths = Enumerable.Range(0, (width + 1) * (height + 1))
            .Select(id => new CoverageSurfaceStrength(id,
                id % (width + 1) is 0 || id % (width + 1) == width || id / (width + 1) is 0 || id / (width + 1) == height ? 0 : 1));
        var authored = new HostCoverageMap(FixtureHash, vertices,
            [new("patch", [new(new(0, 0, 0, triangles.Count), CoverageTriangleRole.Collar)], strengths)],
            [new(new(0, 0, 0, triangles.Count), level)], new("synthetic", "native-recession"));
        // Native fixtures consume the same strict public sidecar and binary formats as callers.
        Map = HostCoverageMapJson.Read(HostCoverageMapJson.Write(authored), "synthetic-map");
        Topology = CoverageRefinement.Build(Source, Map);
        int[] representatives = new int[Topology.PositionGroupCount];
        CoverageReferenceTopology.FillActiveRepresentatives(Topology, [0], representatives, new());
        var scalars = new float[Topology.PositionGroupCount * 2];
        for (int group = 0; group < representatives.Length; group++)
        {
            if (representatives[group] < 0) continue;
            var p = RestPosition(representatives[group]);
            // A continuous tent, zero at the boundary, is independent of the carry implementation.
            float tent = MathF.Min(MathF.Min(p.X + width / 32f, width / 32f - p.X),
                MathF.Min(p.Y + height / 32f, height / 32f - p.Y));
            scalars[group] = tent / 4;
            scalars[Topology.PositionGroupCount + group] = tent / 2;
        }
        string mapHash = HostCoverageMapJson.SemanticSha256(Map);
        var identity = new CoverageRecessionIdentity(FixtureHash, mapHash, FixtureHash,
            CoverageTableBinding.RefinementSha256(Topology), CoverageTableBinding.ArithmeticSha256, FixtureHash);
        byte[] bytes = CoverageRecessionTableBinary.Write(CoverageRecessionTable.Create(identity, representatives, scalars,
            [new(new(10), 0), new(new(20), 1), new(new(30), -1)]));
        TableHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Table = CoverageRecessionTableBinary.Read(bytes, "synthetic-table");
        var bare = new SkinnedGltfMesh(sourceVertices, indices, [Matrix4x4.Identity], [Matrix4x4.Identity], Skeleton);
        Mesh = new(sourceVertices, indices, bare.InverseBind, bare.RestPose, Skeleton)
        { Coverage = HostCoveragePart.Create(Map, Source, 0, bare) };

        void Face((int X, int Y) a, (int X, int Y) b, (int X, int Y) c)
        {
            int start = skin.Count;
            foreach (var p in new[] { a, b, c })
            {
                int index = skin.Count;
                skin.Add(new()
                {
                    Position = new((2 * p.X - width) / 32f, (2 * p.Y - height) / 32f, 0),
                    Normal = Vector3.UnitZ,
                    Tangent = new(1, 0, 0, -1),
                    Color = new(.75f, .5f, .25f, 1),
                    Uv = new(p.X / 16f, p.Y / 16f),
                    BoneWeights = Vector4.UnitX,
                });
                provenance.Add(new(0, 0, index, -1, 0, index));
                vertices.Add(new(0, 0, index, p.Y * (width + 1) + p.X));
            }
            triangles.Add(new(0, 0, triangles.Count, -1, 0, start, start + 1, start + 2));
        }
    }

    internal Vector3 RestPosition(int vertex)
    {
        var v = Topology.Vertices[vertex];
        if (v.SourceVertex >= 0) return Source.Positions[v.SourceVertex];
        var t = Source.Triangles[v.SourceTriangle];
        return (Source.Positions[t.A] * v.A + Source.Positions[t.B] * v.B + Source.Positions[t.C] * v.C) / v.Denominator;
    }

    internal Vector3 ExpectedPosition(int vertex, int entry, Vector3 direction, float transition, Vector3 translation)
    {
        Vector3 p = RestPosition(vertex) + translation;
        int group = Topology.Vertices[vertex].PositionGroup;
        return entry < 0 || Table.Representatives[group] < 0 ? p : p - direction * (Table.Entry(entry)[group] * transition);
    }

    internal Vector3 LeftRampWitness => new(-Width / 32f + 1 / 16f, (2 * (Height / 2) - Height) / 32f, 0);

    internal static (Vector3 Normal, Vector3 Tangent) LeftRampDirections(int entry, Vector3 direction, float transition)
    {
        // On this analytic tent's left ramp, height = x + halfWidth. Recession is height / 4
        // for row zero and height / 2 for row one. Thus the deformed partial derivatives are
        // dP/dx = UnitX - direction * slope and dP/dy = UnitY. Translation has no derivative.
        float slope = entry < 0 ? 0 : transition * (entry == 0 ? .25f : .5f);
        Vector3 dx = Vector3.UnitX - direction * slope;
        return (Vector3.Normalize(Vector3.Cross(dx, Vector3.UnitY)), Vector3.Normalize(dx));
    }

    internal object Extents => new
    {
        Width,
        Height,
        Level,
        sourceVertices = Source.Vertices.Count,
        sourceTriangles = Source.Triangles.Count,
        finalVertices = Topology.VertexCount,
        positionGroups = Topology.PositionGroupCount,
        finalTriangles = Topology.TriangleCount,
        gatherCorners = Topology.Indices.Length,
        maximumPositionFanOut = Topology.Vertices.GroupBy(v => v.PositionGroup).Max(g => g.Count()),
        maximumNormalFanIn = Topology.Indices.ToArray().GroupBy(i => Topology.Vertices[(int)i].ShadingGroup).Max(g => g.Count()),
        fixtureHash = FixtureHash,
        tableHash = TableHash,
    };
}
