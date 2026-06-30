using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class PropCollisionBakeProxyTests
{
    // A solid axis-aligned box group from min..max, as a GltfMesh (12 triangles). Reuses TestMeshes-style
    // construction kept local so the proxy bake test is self-contained.
    static GltfMesh Box(Vector3 min, Vector3 max)
    {
        Vector3[] c =
        {
            new(min.X,min.Y,min.Z), new(max.X,min.Y,min.Z), new(max.X,min.Y,max.Z), new(min.X,min.Y,max.Z),
            new(min.X,max.Y,min.Z), new(max.X,max.Y,min.Z), new(max.X,max.Y,max.Z), new(min.X,max.Y,max.Z),
        };
        int[] tris = { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7 };
        var verts = new ModelVertex[c.Length];
        for (int i = 0; i < c.Length; i++) verts[i] = new ModelVertex { Position = c[i] };
        var idx = new uint[tris.Length];
        for (int i = 0; i < tris.Length; i++) idx[i] = (uint)tris[i];
        return new GltfMesh(verts, idx);
    }

    [Fact]
    public void BakeProxy_OneHullPerGroup_NormalizedIntoRenderFrame()
    {
        // Raw render mesh spans y 0..4 raw; declared height 8 m => scale x2, base already 0, XZ centred on 0.
        GltfMesh renderRaw = Box(new Vector3(-1, 0, -1), new Vector3(1, 4, 1));
        var groups = new List<GltfMesh>
        {
            Box(new Vector3(-1, 0, -1), new Vector3(1, 0.5f, 1)),   // floor slab
            Box(new Vector3(-1, 0, -1), new Vector3(-0.8f, 4, 1)),  // one wall
        };

        var compound = PropCollisionBake.BakeProxy(renderRaw, 8f, groups);
        Assert.Equal(2, compound.Children.Length);

        // Each child is a convex hull, placed at identity (geometry carries position).
        foreach (var child in compound.Children)
        {
            Assert.IsType<ConvexHullShape>(child.Shape);
            Assert.Equal(Quaternion.Identity, child.Local.Orientation);
            Assert.Equal(Vector3.Zero, child.Local.Position);
        }

        // Render-frame normalization: scale 2x means the floor slab's top sits near y=1 (0.5 raw * 2).
        var floorPts = ((ConvexHullShape)compound.Children[0].Shape).Points;
        float maxY = float.MinValue;
        foreach (var p in floorPts) maxY = MathF.Max(maxY, p.Y);
        Assert.InRange(maxY, 0.9f, 1.1f);
    }

    [Fact]
    public void BakeProxy_AllGroupsDegenerate_Throws()
    {
        GltfMesh renderRaw = Box(new Vector3(-1, 0, -1), new Vector3(1, 4, 1));
        // A single flat quad in the y=0 plane: 4 coplanar verts, 2 triangles. Coplanar => skipped.
        var quadVerts = new ModelVertex[]
        {
            new() { Position = new Vector3(-1, 0, -1) }, new() { Position = new Vector3(1, 0, -1) },
            new() { Position = new Vector3(1, 0, 1) },  new() { Position = new Vector3(-1, 0, 1) },
        };
        var quad = new GltfMesh(quadVerts, new uint[] { 0, 1, 2, 0, 2, 3 });
        var groups = new List<GltfMesh> { quad };
        Assert.Throws<InvalidOperationException>(() => PropCollisionBake.BakeProxy(renderRaw, 8f, groups));
    }

    [Fact]
    public void BakeProxy_IsDeterministic_ByteIdenticalReBake()
    {
        GltfMesh renderRaw = Box(new Vector3(-1, 0, -1), new Vector3(1, 4, 1));
        var groups = new List<GltfMesh>
        {
            Box(new Vector3(-1, 0, -1), new Vector3(1, 0.5f, 1)),
            Box(new Vector3(0.8f, 0, -1), new Vector3(1, 4, 1)),
        };

        byte[] Bake()
        {
            var compound = PropCollisionBake.BakeProxy(renderRaw, 8f, groups);
            using var ms = new MemoryStream();
            PropCollisionBake.Write(compound, ms);
            return ms.ToArray();
        }

        Assert.Equal(Bake(), Bake());
    }
}
