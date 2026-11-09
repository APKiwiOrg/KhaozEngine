using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapInventoryAliasTests
{
    [Fact]
    public void PresentationInventory_CountsSharedShapeArraysOnce()
    {
        int[] indices = [0, 1, 2];
        var first = new KhaozEngine.Physics.TriangleMeshShape(new Vector3[3], indices);
        var second = new KhaozEngine.Physics.TriangleMeshShape(new Vector3[3], indices);
        Assert.Same(first.Indices, second.Indices);
        var inventory = new MapPresentationShapeInventory();
        Assert.Equal(176, inventory.Shape(first));
        Assert.Equal(164, inventory.Shape(second));
        Assert.Equal(0, inventory.Shape(first));
    }
    [Fact]
    public void LayerInventory_CountsSharedTierRowsAndHandleArraysOnce()
    {
        var tier = new NativePropLod(0, new[] { new MeshHandle(1) });
        var lods = new Dictionary<string, IReadOnlyList<NativePropLod>> { ["invented"] = new[] { tier } };
        var row = new NativePropInstance("one", null, "invented", Matrix4x4.Identity, new(0, 0, 0));
        var first = PropLayer.PlacementLayer(new[] { row }, lods, 10, []);
        var second = PropLayer.PlacementLayer(new[] { row }, lods, 10, []);
        var inventory = new MapNativeInventory();
        long actual = inventory.Layer(first) + inventory.Layer(second);
        // Two instance arrays, two dictionaries and tier-reference arrays, one shared tier and handle array.
        long expected = 2 * 128L + 2 * 64L + 2 * System.IntPtr.Size + 24 + Unsafe.SizeOf<MeshHandle>();
        Assert.Equal(expected, actual);
    }
}
