using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Headless tests for deriving a static-collision footprint from a (PropLoader-normalized) prop mesh:
/// short props use the full XZ footprint, tall props use the bottom trunk slice (so a tree's canopy is ignored),
/// and aspect ratio picks a cylinder vs an oriented box. Synthetic meshes (only vertex positions matter).</summary>
public class PropFootprintTests
{
    // Build a mesh from key vertex positions (Derive reads only positions). Base is assumed at y=0.
    static GltfMesh Mesh(params Vector3[] positions)
    {
        var verts = new ModelVertex[positions.Length];
        for (int i = 0; i < positions.Length; i++)
            verts[i] = new ModelVertex(positions[i], Vector3.UnitY, Vector4.One);
        return new GltfMesh(verts, new uint[] { 0, 0, 0 });
    }

    // A box's 8 corners spanning x in [-hx,hx], y in [y0,y1], z in [-hz,hz].
    static IEnumerable<Vector3> Box(float hx, float y0, float y1, float hz)
    {
        foreach (float x in new[] { -hx, hx })
            foreach (float y in new[] { y0, y1 })
                foreach (float z in new[] { -hz, hz })
                    yield return new Vector3(x, y, z);
    }

    [Fact]
    public void TallTree_UsesTrunkSlice_IgnoresCanopy()
    {
        // 10 m tree: a narrow trunk (hx=hz=0.3) from y=0..2, a wide canopy (hx=hz=3) from y=2..10.
        var verts = new List<Vector3>();
        verts.AddRange(Box(0.3f, 0f, 2f, 0.3f));
        verts.AddRange(Box(3f, 2f, 10f, 3f));
        ColliderShape s = PropFootprint.Derive(Mesh(verts.ToArray()));
        Assert.Equal(ColliderKind.Cylinder, s.Kind);
        Assert.Equal(0.3f, s.Radius, 2); // trunk, not the 3 m canopy
    }

    [Fact]
    public void ShortRoundProp_UsesFullFootprint_AsCylinder()
    {
        // 1.5 m rock, near-round footprint hx=1.2 hz=1.15 (aspect ~1.04 < threshold).
        ColliderShape s = PropFootprint.Derive(Mesh(new List<Vector3>(Box(1.2f, 0f, 1.5f, 1.15f)).ToArray()));
        Assert.Equal(ColliderKind.Cylinder, s.Kind);
        Assert.Equal(1.2f, s.Radius, 2); // max half-extent (never under-covers)
    }

    [Fact]
    public void ShortOblongProp_UsesBox()
    {
        // 1.5 m slab, oblong footprint hx=2 hz=0.8 (aspect 2.5 > threshold) -> oriented box.
        ColliderShape s = PropFootprint.Derive(Mesh(new List<Vector3>(Box(2f, 0f, 1.5f, 0.8f)).ToArray()));
        Assert.Equal(ColliderKind.Box, s.Kind);
        Assert.Equal(2f, s.HalfW, 2);
        Assert.Equal(0.8f, s.HalfD, 2);
    }

    [Fact]
    public void TallUniformBuilding_FullFootprintViaTrunkSlice()
    {
        // 5 m building with vertical walls hx=3 hz=1.5 at every height -> the bottom 1 m slice equals the full
        // footprint, so an oriented box(3, 1.5) (aspect 2 > threshold) is derived.
        ColliderShape s = PropFootprint.Derive(Mesh(new List<Vector3>(Box(3f, 0f, 5f, 1.5f)).ToArray()));
        Assert.Equal(ColliderKind.Box, s.Kind);
        Assert.Equal(3f, s.HalfW, 2);
        Assert.Equal(1.5f, s.HalfD, 2);
    }

    [Fact]
    public void DeriveAll_ExplicitColliderWins_NoLoadNeeded()
    {
        // Every entry declares an explicit collider, so DeriveAll returns those without loading any glTF file.
        const string json = """
        { "props": [
            { "id": "pine", "file": "missing.glb", "heightMeters": 6, "collider": { "type": "cylinder", "radius": 0.5 } },
            { "id": "inn",  "file": "missing.glb", "heightMeters": 5, "collider": { "type": "box", "halfW": 3, "halfD": 2 } }
        ] }
        """;
        AssetManifest m = AssetManifest.Parse(json);
        IReadOnlyDictionary<string, ColliderShape> shapes = PropFootprint.DeriveAll(m);
        Assert.Equal(ColliderKind.Cylinder, shapes["pine"].Kind);
        Assert.Equal(0.5f, shapes["pine"].Radius, 3);
        Assert.Equal(ColliderKind.Box, shapes["inn"].Kind);
        Assert.Equal(3f, shapes["inn"].HalfW, 3);
    }
}
