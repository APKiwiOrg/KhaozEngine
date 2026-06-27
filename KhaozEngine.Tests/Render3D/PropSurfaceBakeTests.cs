using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class PropSurfaceBakeTests
{
    // A solid axis-aligned box from y0..y1 over [-hx,hx] x [-hz,hz], fully triangulated (12 triangles).
    static (List<ModelVertex> v, List<uint> i) BoxGeo(float hx, float y0, float y1, float hz, uint baseIndex = 0)
    {
        var v = new List<ModelVertex>();
        foreach (float y in new[] { y0, y1 })
            foreach (float z in new[] { -hz, hz })
                foreach (float x in new[] { -hx, hx })
                    v.Add(new ModelVertex(new Vector3(x, y, z), Vector3.UnitY, Vector4.One));
        // 8 corners: index = (y?4:0)+(z?2:0)+(x?1:0). Two triangles per face; the top face is what the bake reads.
        var quads = new[]
        {
            new[] { 4, 5, 7, 6 }, // top  (+Y)
            new[] { 0, 2, 3, 1 }, // bottom (-Y)
            new[] { 0, 1, 5, 4 }, // -Z
            new[] { 2, 6, 7, 3 }, // +Z
            new[] { 0, 4, 6, 2 }, // -X
            new[] { 1, 3, 7, 5 }, // +X
        };
        var i = new List<uint>();
        foreach (int[] q in quads)
        {
            i.Add(baseIndex + (uint)q[0]); i.Add(baseIndex + (uint)q[1]); i.Add(baseIndex + (uint)q[2]);
            i.Add(baseIndex + (uint)q[0]); i.Add(baseIndex + (uint)q[2]); i.Add(baseIndex + (uint)q[3]);
        }
        return (v, i);
    }

    static GltfMesh Box(float hx, float y0, float y1, float hz)
    {
        (var v, var i) = BoxGeo(hx, y0, y1, hz);
        return new GltfMesh(v.ToArray(), i.ToArray());
    }

    // A 1.5 m tall, 2 m square flat-topped slab centred on origin (a "rock").
    static GltfMesh Slab() => Box(1f, 0f, 1.5f, 1f);

    [Fact]
    public void Bake_FlatSlab_TopIsSlabHeight()
    {
        PropSurface s = PropSurfaceBake.Bake(Slab());
        Assert.Equal(1.5f, s.MaxHeight, 1);
        Assert.Equal(1.5f, s.SampleLocal(0f, 0f)!.Value, 1); // standing over the centre -> the slab top
    }

    [Fact]
    public void IsWalkableSolid_ShortSlab_True()
    {
        Assert.True(PropSurfaceBake.IsWalkableSolid(Slab()));
    }

    [Fact]
    public void IsWalkableSolid_TallThinTrunkWithCanopy_False()
    {
        // A tall thin trunk (hx=hz=0.3, 0..2) + a wide canopy (hx=hz=3, 2..10): tree-like -> not a walkable solid.
        (var trunkV, var trunkI) = BoxGeo(0.3f, 0f, 2f, 0.3f, baseIndex: 0);
        (var canopyV, var canopyI) = BoxGeo(3f, 2f, 10f, 3f, baseIndex: 8);
        var v = new List<ModelVertex>(trunkV); v.AddRange(canopyV);
        var i = new List<uint>(trunkI); i.AddRange(canopyI);
        Assert.False(PropSurfaceBake.IsWalkableSolid(new GltfMesh(v.ToArray(), i.ToArray())));
    }
}
