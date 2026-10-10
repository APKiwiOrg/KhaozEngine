using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Each feature query limit the builder measures, at and just past its boundary.</summary>
public class MapWorldDiagnosticsTests
{
    [Fact]
    public void TerrainChunkBeyondTheAnchorExtent_IsLocalExtent()
    {
        MapBuiltWorld world = NativeWorldFixtures.BuildTallCornerCell();
        Assert.NotEmpty(world.Terrain.Chunks);
        Assert.All(world.Terrain.Chunks, chunk =>
            Assert.Contains(new MapStaticDiagnostic(chunk.ChunkId, MapFeatureQuerySupport.LocalExtent), world.Diagnostics));
    }

    [Fact]
    public void MeshOverTheTriangleCap_IsMeshTriangleCapacity()
    {
        Assert.Empty(MapWorldBuilder.Measure(Mesh(65_536)));
        Assert.Equal(new[] { MapFeatureQuerySupport.MeshTriangleCapacity }, MapWorldBuilder.Measure(Mesh(65_537)));
    }

    [Fact]
    public void HullSpanningMoreThan64Metres_IsLocalExtent()
    {
        Assert.Empty(MapWorldBuilder.Measure(Hull(64f)));
        Assert.Equal(new[] { MapFeatureQuerySupport.LocalExtent }, MapWorldBuilder.Measure(Hull(64.5f)));
    }

    [Fact]
    public void HullOverOneHundredThirtyPoints_IsHullCapacity()
    {
        Assert.Empty(MapWorldBuilder.Measure(Sphere(130)));
        Assert.Equal(new[] { MapFeatureQuerySupport.HullCapacity }, MapWorldBuilder.Measure(Sphere(131)));
    }

    [Fact]
    public void NestedCompoundFlatteningPast64Leaves_IsLeafCapacity()
    {
        Assert.Empty(MapWorldBuilder.Measure(Nested(32, 32)));
        Assert.Equal(new[] { MapFeatureQuerySupport.LeafCapacity }, MapWorldBuilder.Measure(Nested(32, 33)));
    }

    // One small triangle repeated, so only the count matters.
    static TriangleMeshShape Mesh(int triangles) => new(new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitZ },
        Enumerable.Range(0, triangles).SelectMany(_ => new[] { 0, 1, 2 }).ToArray());

    // A box hull from the origin to (length, 1, 1). Every corner lies within 64 m of the origin only when length is.
    static ConvexHullShape Hull(float length) => new(Enumerable.Range(0, 8)
        .Select(i => new Vector3((i & 1) == 0 ? 0f : length, (i & 2) == 0 ? 0f : 1f, (i & 4) == 0 ? 0f : 1f)).ToArray());

    // Points spread over a unit sphere, so every one is a hull vertex and only the count matters.
    static ConvexHullShape Sphere(int points) => new(Enumerable.Range(0, points).Select(i =>
    {
        float y = 1f - 2f * (i + 0.5f) / points, r = MathF.Sqrt(1f - y * y), a = i * 2.39996323f;
        return new Vector3(r * MathF.Cos(a), y, r * MathF.Sin(a));
    }).ToArray());

    // Two top-level children, each a compound of small boxes.
    static CompoundShape Nested(int first, int second) => NativeWorldFixtures.Compound(
        (Row(first), Vector3.Zero), (Row(second), new Vector3(0f, 0f, 1f)));

    static CompoundShape Row(int boxes) => NativeWorldFixtures.Compound(Enumerable.Range(0, boxes)
        .Select(i => ((PhysicsShape)NativeWorldFixtures.Box(0.1f, 1f, 0.2f), new Vector3(0.1f * i, 0.5f, 0f))).ToArray());
}
