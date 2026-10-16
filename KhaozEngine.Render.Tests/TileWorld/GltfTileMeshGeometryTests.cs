using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The glb-backed clearance mesh source: positions and triangles off the loader with node transforms
/// baked, and every bad reference or bad file answered as a false return with a reason, never a throw.</summary>
public class GltfTileMeshGeometryTests : IDisposable
{
    readonly string _root;

    public GltfTileMeshGeometryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ke-clearance-glb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "kit"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    string KitPath(string name) => Path.Combine(_root, "kit", name);

    static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(Vector3.Distance(expected, actual), 0f, 1e-4f);
    }

    [Fact]
    public void LoadsAnIndexedBox()
    {
        var min = new Vector3(-0.5f, 0f, -0.25f);
        var max = new Vector3(0.5f, 2f, 0.25f);
        ClearanceGlbFixtures.WriteBox(KitPath("box.glb"), min, max);

        Assert.True(new GltfTileMeshGeometry(_root).TryLoad("kit/box.glb", out ClearanceMesh? mesh, out string? error), error);

        Assert.Null(error);
        Assert.Equal(8, mesh.Positions.Count);
        Assert.Equal(12, mesh.Indices.Count / 3);
        AssertNear(min, mesh.Min);
        AssertNear(max, mesh.Max);
    }

    [Fact]
    public void LoadsANonIndexedPrimitive()
    {
        ClearanceGlbFixtures.WriteBox(KitPath("soup.glb"), Vector3.Zero, Vector3.One, indexed: false);

        Assert.True(new GltfTileMeshGeometry(_root).TryLoad("kit/soup.glb", out ClearanceMesh? mesh, out string? error), error);

        Assert.Equal(12, mesh.Indices.Count / 3);
        AssertNear(Vector3.Zero, mesh.Min);
        AssertNear(Vector3.One, mesh.Max);
    }

    [Fact]
    public void BakesNodeTransforms()
    {
        ClearanceGlbFixtures.WriteBox(KitPath("moved.glb"), Vector3.Zero, Vector3.One, translation: new Vector3(2f, 0f, 0f));

        Assert.True(new GltfTileMeshGeometry(_root).TryLoad("kit/moved.glb", out ClearanceMesh? mesh, out string? error), error);

        Assert.InRange(mesh.Min.X, 2f - 1e-4f, 2f + 1e-4f);
        Assert.InRange(mesh.Max.X, 3f - 1e-4f, 3f + 1e-4f);
    }

    [Fact]
    public void MissingFileIsAnError()
    {
        Assert.False(new GltfTileMeshGeometry(_root).TryLoad("kit/absent.glb", out ClearanceMesh? mesh, out string? error));

        Assert.Null(mesh);
        Assert.Contains("kit/absent.glb", error, StringComparison.Ordinal);
        Assert.Contains(KitPath("absent.glb"), error, StringComparison.Ordinal);
        Assert.Contains("missing", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptFileIsAnError()
    {
        var bytes = new byte[256];
        new Random(4).NextBytes(bytes);
        File.WriteAllBytes(KitPath("corrupt.glb"), bytes);

        Assert.False(new GltfTileMeshGeometry(_root).TryLoad("kit/corrupt.glb", out ClearanceMesh? mesh, out string? error));

        Assert.Null(mesh);
        Assert.Contains("kit/corrupt.glb", error, StringComparison.Ordinal);
        Assert.Contains(KitPath("corrupt.glb"), error, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyGeometryIsAnError()
    {
        ClearanceGlbFixtures.WritePointsOnly(KitPath("points.glb"));

        Assert.False(new GltfTileMeshGeometry(_root).TryLoad("kit/points.glb", out ClearanceMesh? mesh, out string? error));

        Assert.Null(mesh);
        Assert.Contains("kit/points.glb", error, StringComparison.Ordinal);
        Assert.Contains("triangles", error, StringComparison.Ordinal);
    }

    [Fact]
    public void NonFinitePositionIsAnError()
    {
        // A finite but huge node scale overflows the baked positions, so the loader succeeds and the mesh does not.
        ClearanceGlbFixtures.WriteBox(KitPath("huge.glb"), Vector3.Zero, new Vector3(10f), scale: new Vector3(1e38f, 1f, 1f));

        Assert.False(new GltfTileMeshGeometry(_root).TryLoad("kit/huge.glb", out ClearanceMesh? mesh, out string? error));

        Assert.Null(mesh);
        Assert.Contains("kit/huge.glb", error, StringComparison.Ordinal);
        Assert.Contains("not finite", error, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedMeshRefIsAnError()
    {
        const string meshRef = "kit/bad\0name.glb";

        Assert.False(new GltfTileMeshGeometry(_root).TryLoad(meshRef, out ClearanceMesh? mesh, out string? error));

        Assert.Null(mesh);
        Assert.Contains(meshRef, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankMeshRefIsAnError(string meshRef)
    {
        Assert.False(new GltfTileMeshGeometry(_root).TryLoad(meshRef, out ClearanceMesh? mesh, out string? error));

        Assert.Null(mesh);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void CachesByMeshRefIncludingFailures()
    {
        var geometry = new GltfTileMeshGeometry(_root);
        ClearanceGlbFixtures.WriteBox(KitPath("box.glb"), Vector3.Zero, Vector3.One);

        Assert.True(geometry.TryLoad("kit/box.glb", out ClearanceMesh? first, out _));
        Assert.True(geometry.TryLoad("kit/box.glb", out ClearanceMesh? second, out _));
        Assert.Same(first, second);

        Assert.False(geometry.TryLoad("kit/late.glb", out _, out string? firstError));
        ClearanceGlbFixtures.WriteBox(KitPath("late.glb"), Vector3.Zero, Vector3.One);
        Assert.False(geometry.TryLoad("kit/late.glb", out _, out string? secondError));
        Assert.Equal(firstError, secondError);
    }
}
