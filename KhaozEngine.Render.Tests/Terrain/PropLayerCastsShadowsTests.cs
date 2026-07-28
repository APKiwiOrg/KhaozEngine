using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>
    /// Headless coverage of the per-layer casts-shadows policy (issue #287): the <see cref="PropLayer.CastsShadows"/>
    /// flag every layer factory carries, and the <c>castsShadows</c> argument <see cref="PropRenderer"/> stamps onto
    /// each queued instance. What the depth pass then does with the flag is Scene3D's half (ShadowCasterPolicyTests);
    /// this file proves the knob reaches the queue at all, which is the seam a consumer wires.
    /// </summary>
    public sealed class PropLayerCastsShadowsTests
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

        static List<PropPlacement> One(string id = "bush_a") =>
            new() { new PropPlacement(id, 2f, 0f, 3f, 1f, 0f, 0) };

        [Fact]
        public void Every_layer_kind_casts_by_default()
        {
            var scatter = new ScatterConfig();
            var companions = new CompanionConfig();
            var meshes = Meshes(("bush_a", 1));
            var parts = Parts("bush_a", 1, 2);

            Assert.True(PropLayer.ScatterLayer(scatter, meshes, 40f).CastsShadows);
            Assert.True(PropLayer.ScatterLayer(scatter, parts, 40f).CastsShadows);
            Assert.True(PropLayer.CompanionLayer(0, companions, meshes, 40f).CastsShadows);
            Assert.True(PropLayer.CompanionLayer(0, companions, parts, 40f).CastsShadows);
            Assert.True(PropLayer.PlacementLayer(One(), meshes, 40f).CastsShadows);
            Assert.True(PropLayer.PlacementLayer(One(), parts, 40f).CastsShadows);
        }

        [Fact]
        public void Every_layer_kind_can_opt_out()
        {
            var scatter = new ScatterConfig();
            var companions = new CompanionConfig();
            var meshes = Meshes(("bush_a", 1));
            var parts = Parts("bush_a", 1, 2);

            Assert.False(PropLayer.ScatterLayer(scatter, meshes, 40f, castsShadows: false).CastsShadows);
            Assert.False(PropLayer.ScatterLayer(scatter, parts, 40f, castsShadows: false).CastsShadows);
            Assert.False(PropLayer.CompanionLayer(0, companions, meshes, 40f, castsShadows: false).CastsShadows);
            Assert.False(PropLayer.CompanionLayer(0, companions, parts, 40f, castsShadows: false).CastsShadows);
            Assert.False(PropLayer.PlacementLayer(One(), meshes, 40f, castsShadows: false).CastsShadows);
            Assert.False(PropLayer.PlacementLayer(One(), parts, 40f, castsShadows: false).CastsShadows);
        }

        [Fact]
        public void The_hlod_copy_keeps_the_policy()
        {
            // WithHlod rebuilds the layer, so a dropped field here would silently re-enable casting on exactly the
            // layers that use HLOD - the dense ones the flag exists for.
            var layer = PropLayer.ScatterLayer(new ScatterConfig(), Meshes(("bush_a", 1)), 40f, castsShadows: false);
            var withHlod = layer.WithHlod(new Dictionary<string, GltfMesh>(), hlodDistance: 200f, weldCell: 1.5f);
            Assert.False(withHlod.CastsShadows);
            Assert.True(withHlod.HasHlod);
        }

        [Fact]
        public void Queued_props_carry_the_flag()
        {
            var si = new SceneInstances();
            PropRenderer.Queue(si, One(), Meshes(("bush_a", 4)), Vector3.Zero, drawRadius: 40f, castsShadows: false);
            Assert.Single(si.Items);
            Assert.False(si.Items[0].CastsShadows);
            Assert.False(si.Items[0].Dissolving);   // opting out is not a dissolve: the prop still draws solid
        }

        [Fact]
        public void Queued_props_cast_by_default()
        {
            var si = new SceneInstances();
            PropRenderer.Queue(si, One(), Meshes(("bush_a", 4)), Vector3.Zero, drawRadius: 40f);
            Assert.Single(si.Items);
            Assert.True(si.Items[0].CastsShadows);
        }

        [Fact]
        public void A_faded_non_casting_prop_keeps_both()
        {
            // Inside the fade band an opted-out prop must keep its dissolve AND stay a non-caster: the two knobs are
            // independent, and the emit path folds them into one Add call.
            var placements = new List<PropPlacement> { new PropPlacement("bush_a", 36f, 0f, 0f, 1f, 0f, 0) };
            var si = new SceneInstances();
            PropRenderer.Queue(si, placements, Meshes(("bush_a", 4)), Vector3.Zero, drawRadius: 40f,
                fadeBandWidth: 12f, castsShadows: false);
            Assert.Single(si.Items);
            Assert.False(si.Items[0].CastsShadows);
            Assert.True(si.Items[0].DissolveThreshold > 0f);
        }

        [Fact]
        public void Every_part_of_a_multipart_prop_carries_the_flag()
        {
            var si = new SceneInstances();
            PropRenderer.Queue(si, One(), Parts("bush_a", 4, 5), Vector3.Zero, drawRadius: 40f, castsShadows: false);
            Assert.Equal(2, si.Items.Count);
            foreach (var item in si.Items) Assert.False(item.CastsShadows);
        }
    }
}
