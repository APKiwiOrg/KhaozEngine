using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Clearance;

/// <summary>Headless tests for the clearance geometry core: mesh validation, the filled solid and its depth
/// query, triangle sampling and the penetration probe under transforms.</summary>
public class ClearanceGeometryTests
{
    const float Cell = 0.1f;

    [Fact]
    public void MeshRefusesBadInput()
    {
        var tri = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitZ };

        Assert.ThrowsAny<ArgumentException>(() => new ClearanceMesh(tri, new[] { 0, 1 }));
        Assert.ThrowsAny<ArgumentException>(() => new ClearanceMesh(tri, new[] { 0, 1, 3 }));
        Assert.ThrowsAny<ArgumentException>(() => new ClearanceMesh(tri, new[] { 0, -1, 2 }));
        Assert.ThrowsAny<ArgumentException>(() =>
            new ClearanceMesh(new[] { Vector3.Zero, new Vector3(float.NaN, 0f, 0f), Vector3.UnitZ }, new[] { 0, 1, 2 }));
        Assert.ThrowsAny<ArgumentException>(() => new ClearanceMesh(tri, Array.Empty<int>()));
    }

    [Fact]
    public void BoxDepthIsTheNearestExit()
    {
        var solid = ClearanceSolid.Build(Box(new Vector3(0f, 0f, 0f), new Vector3(1f, 4f, 0.3f)), Cell);

        Assert.Equal(0.15f, solid.DepthAt(new Vector3(0.5f, 2f, 0.15f)), 0.051f);
        Assert.Equal(0.05f, solid.DepthAt(new Vector3(0.5f, 3.95f, 0.15f)), 0.051f);
        Assert.Equal(0f, solid.DepthAt(new Vector3(0.5f, 4.2f, 0.15f)));
        Assert.Equal(0f, solid.DepthAt(new Vector3(1.4f, 2f, 0.15f)));
    }

    [Fact]
    public void GableAtticCountsAsSolid()
    {
        // Width 4 along x, depth 3 along z, eave at 2 and ridge at 4 over x = 2.
        var solid = ClearanceSolid.Build(Gable(4f, 3f, 2f, 4f), Cell);

        Assert.True(solid.DepthAt(new Vector3(2f, 3f, 1.5f)) > 0f);
        // The slope at x = 0.3 is only 2.3 high, so a point at 3.5 near the eave is in open air.
        Assert.Equal(0f, solid.DepthAt(new Vector3(0.2f, 3.5f, 1.5f)));
    }

    [Fact]
    public void TooLargeGridThrows()
    {
        var mesh = Box(Vector3.Zero, new Vector3(500f, 1f, 500f));

        var ex = Assert.Throws<ClearanceTooLargeException>(() => ClearanceSolid.Build(mesh, Cell));
        Assert.True(ex.Cells > ClearanceSolid.MaxCells);
    }

    [Fact]
    public void DeepestRespectsTolerance()
    {
        // A thin obstacle caps depth near half its thickness, so the subject reaches into a thick block where
        // the 0.2 m reach is the nearest exit.
        var obstacle = ClearanceSolid.Build(Box(Vector3.Zero, new Vector3(4f, 4f, 4f)), Cell);
        var subject = ClearanceSamples.Build(Box(new Vector3(3.8f, 1f, 1f), new Vector3(4.8f, 2f, 2f)), Cell / 2f);

        var hit = ClearanceProbe.Deepest(subject, Matrix4x4.Identity, obstacle, Matrix4x4.Identity, tolerance: 0.1f);
        Assert.NotNull(hit);
        Assert.Equal(0.2f, hit.Value.Depth, 0.06f);

        Assert.Null(ClearanceProbe.Deepest(subject, Matrix4x4.Identity, obstacle, Matrix4x4.Identity, tolerance: 0.3f));
    }

    [Fact]
    public void DeepestFollowsBothTransforms()
    {
        var obstacle = ClearanceSolid.Build(Box(Vector3.Zero, new Vector3(4f, 4f, 4f)), Cell);
        var subject = ClearanceSamples.Build(Box(new Vector3(3.8f, 1f, 1f), new Vector3(4.8f, 2f, 2f)), Cell / 2f);
        var pair = Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(10f, 0f, 0f);

        var still = ClearanceProbe.Deepest(subject, Matrix4x4.Identity, obstacle, Matrix4x4.Identity, tolerance: 0.1f);
        var moved = ClearanceProbe.Deepest(subject, pair, obstacle, pair, tolerance: 0.1f);

        Assert.NotNull(still);
        Assert.NotNull(moved);
        Assert.Equal(still.Value.Depth, moved.Value.Depth, 0.02f);
    }

    [Fact]
    public void PlanarBoundsOfAQuarterTurn()
    {
        var (min, max) = ClearanceProbe.PlanarBounds(
            Vector3.Zero, new Vector3(2f, 1f, 1f), Matrix4x4.CreateRotationY(MathF.PI / 2f));

        Assert.Equal(1f, max.X - min.X, 0.0001f);
        Assert.Equal(2f, max.Y - min.Y, 0.0001f);
    }

    /// <summary>A closed axis-aligned box as 12 triangles.</summary>
    static ClearanceMesh Box(Vector3 min, Vector3 max)
    {
        var p = new List<Vector3>(8);
        for (int i = 0; i < 8; i++)
        {
            p.Add(new Vector3(
                (i & 1) == 0 ? min.X : max.X,
                (i & 2) == 0 ? min.Y : max.Y,
                (i & 4) == 0 ? min.Z : max.Z));
        }

        var idx = new List<int>(36);
        Quad(idx, 0, 2, 6, 4); // -x
        Quad(idx, 1, 5, 7, 3); // +x
        Quad(idx, 0, 4, 5, 1); // -y
        Quad(idx, 2, 3, 7, 6); // +y
        Quad(idx, 0, 1, 3, 2); // -z
        Quad(idx, 4, 6, 7, 5); // +z
        return new ClearanceMesh(p, idx);
    }

    /// <summary>A closed triangular prism: the gable cross-section spans x 0..width with its base at the eave and
    /// its apex at the ridge over the middle, extruded along z 0..depth.</summary>
    static ClearanceMesh Gable(float width, float depth, float eaveY, float ridgeY)
    {
        var p = new List<Vector3>
        {
            new(0f, eaveY, 0f), new(width, eaveY, 0f), new(width / 2f, ridgeY, 0f),
            new(0f, eaveY, depth), new(width, eaveY, depth), new(width / 2f, ridgeY, depth),
        };

        var idx = new List<int> { 0, 2, 1, 3, 4, 5 };
        Quad(idx, 0, 1, 4, 3); // base
        Quad(idx, 0, 3, 5, 2); // left slope
        Quad(idx, 1, 2, 5, 4); // right slope
        return new ClearanceMesh(p, idx);
    }

    static void Quad(List<int> idx, int a, int b, int c, int d)
    {
        idx.Add(a);
        idx.Add(b);
        idx.Add(c);
        idx.Add(a);
        idx.Add(c);
        idx.Add(d);
    }
}
