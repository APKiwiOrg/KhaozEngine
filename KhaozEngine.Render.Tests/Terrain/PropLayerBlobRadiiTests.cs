using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>
    /// Headless coverage of the per-layer blob-radii seam (issue #388): the <see cref="PropLayer.BlobRadii"/> table
    /// every layer factory carries. What <see cref="PropRenderer"/> does with the table (per-kit lookup, scale
    /// multiplication, mode gating) is covered separately in PropRendererBlobTests; this file proves the table
    /// reaches the layer at all and survives <see cref="PropLayer.WithHlod"/>, the seam a consumer wires.
    /// </summary>
    public sealed class PropLayerBlobRadiiTests
    {
        static Dictionary<string, MeshHandle> Meshes(params (string id, int slot)[] entries)
        {
            var d = new Dictionary<string, MeshHandle>();
            foreach (var (id, slot) in entries) d[id] = new MeshHandle(slot);
            return d;
        }

        static Dictionary<string, IReadOnlyList<MeshHandle>> Parts(string id, params int[] slots)
        {
            var list = new List<MeshHandle>();
            foreach (int s in slots) list.Add(new MeshHandle(s));
            return new Dictionary<string, IReadOnlyList<MeshHandle>> { [id] = list };
        }

        static List<PropPlacement> One(string id = "pine_a") =>
            new() { new PropPlacement(id, 2f, 0f, 3f, 1f, 0f, 0) };

        [Fact]
        public void Every_layer_kind_has_no_blob_radii_by_default()
        {
            var scatter = new ScatterConfig();
            var companions = new CompanionConfig();
            var meshes = Meshes(("pine_a", 1));
            var parts = Parts("pine_a", 1, 2);

            Assert.Null(PropLayer.ScatterLayer(scatter, meshes, 40f).BlobRadii);
            Assert.Null(PropLayer.ScatterLayer(scatter, parts, 40f).BlobRadii);
            Assert.Null(PropLayer.CompanionLayer(0, companions, meshes, 40f).BlobRadii);
            Assert.Null(PropLayer.CompanionLayer(0, companions, parts, 40f).BlobRadii);
            Assert.Null(PropLayer.PlacementLayer(One(), meshes, 40f).BlobRadii);
            Assert.Null(PropLayer.PlacementLayer(One(), parts, 40f).BlobRadii);
        }

        [Fact]
        public void Every_layer_kind_can_opt_in()
        {
            var scatter = new ScatterConfig();
            var companions = new CompanionConfig();
            var meshes = Meshes(("pine_a", 1));
            var parts = Parts("pine_a", 1, 2);
            var radii = new Dictionary<string, float> { ["pine_a"] = 1.5f };

            Assert.Same(radii, PropLayer.ScatterLayer(scatter, meshes, 40f, blobRadii: radii).BlobRadii);
            Assert.Same(radii, PropLayer.ScatterLayer(scatter, parts, 40f, blobRadii: radii).BlobRadii);
            Assert.Same(radii, PropLayer.CompanionLayer(0, companions, meshes, 40f, blobRadii: radii).BlobRadii);
            Assert.Same(radii, PropLayer.CompanionLayer(0, companions, parts, 40f, blobRadii: radii).BlobRadii);
            Assert.Same(radii, PropLayer.PlacementLayer(One(), meshes, 40f, blobRadii: radii).BlobRadii);
            Assert.Same(radii, PropLayer.PlacementLayer(One(), parts, 40f, blobRadii: radii).BlobRadii);
        }

        [Fact]
        public void The_hlod_copy_keeps_the_table()
        {
            // WithHlod rebuilds the layer, so a dropped field here would silently drop blob registration on
            // exactly the layers dense enough to use HLOD.
            var radii = new Dictionary<string, float> { ["pine_a"] = 1.5f };
            var layer = PropLayer.ScatterLayer(new ScatterConfig(), Meshes(("pine_a", 1)), 40f, blobRadii: radii);
            var withHlod = layer.WithHlod(new Dictionary<string, GltfMesh>(), hlodDistance: 200f, weldCell: 1.5f);
            Assert.Same(radii, withHlod.BlobRadii);
            Assert.True(withHlod.HasHlod);
        }
    }
}
