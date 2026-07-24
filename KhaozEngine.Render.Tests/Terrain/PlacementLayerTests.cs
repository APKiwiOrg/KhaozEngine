using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Covers <see cref="PropLayer.PlacementLayer(IReadOnlyList{PropPlacement}, IReadOnlyDictionary{string, MeshHandle}, float, float, IReadOnlyDictionary{string, MeshHandle}, float, bool)"/>
    /// and its multi-part overload (issue #286): the frozen, author-supplied placement kind, its collider opt-out,
    /// and that <see cref="PropLayer.WithHlod"/> carries both through unchanged. Plain unit tests, no GPU.</summary>
    public class PlacementLayerTests
    {
        static IReadOnlyDictionary<string, MeshHandle> NoMeshes() => new Dictionary<string, MeshHandle>();

        static IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> NoPartMeshes() =>
            new Dictionary<string, IReadOnlyList<MeshHandle>>();

        static IReadOnlyList<PropPlacement> OnePlacement() =>
            new[] { new PropPlacement("tree", 1f, 0f, 2f, 1f, 0f, 0) };

        [Fact]
        public void PlacementLayer_NullPlacements_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                PropLayer.PlacementLayer(null!, NoMeshes(), 90f));
            Assert.Throws<ArgumentNullException>(() =>
                PropLayer.PlacementLayer(null!, NoPartMeshes(), 90f));
        }

        [Fact]
        public void PlacementLayer_NullMeshes_Throws()
        {
            IReadOnlyList<PropPlacement> placements = OnePlacement();
            Assert.Throws<ArgumentNullException>(() =>
                PropLayer.PlacementLayer(placements, (IReadOnlyDictionary<string, MeshHandle>)null!, 90f));
            Assert.Throws<ArgumentNullException>(() =>
                PropLayer.PlacementLayer(placements, (IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>)null!, 90f));
        }

        [Fact]
        public void PlacementLayer_StoresKnobs()
        {
            IReadOnlyList<PropPlacement> placements = OnePlacement();
            IReadOnlyDictionary<string, MeshHandle> meshes = NoMeshes();
            IReadOnlyDictionary<string, MeshHandle> lodMeshes = NoMeshes();

            PropLayer layer = PropLayer.PlacementLayer(placements, meshes, 120f, 15f, lodMeshes, 60f);

            Assert.Same(placements, layer.Placements);
            Assert.Equal(120f, layer.DrawRadius);
            Assert.Equal(15f, layer.FadeBandWidth);
            Assert.Same(lodMeshes, layer.LodMeshes);
            Assert.Equal(60f, layer.LodDistance);
            Assert.True(layer.IsPlacement);
            Assert.False(layer.IsCompanion);
            Assert.Null(layer.Scatter);
            Assert.Null(layer.Companions);
            Assert.True(layer.RegisterColliders);
        }

        [Fact]
        public void PlacementLayer_CollidersOptOut()
        {
            PropLayer layer = PropLayer.PlacementLayer(OnePlacement(), NoMeshes(), 90f, colliders: false);
            Assert.False(layer.RegisterColliders);
        }

        [Fact]
        public void PlacementLayer_MultiPart_StoresPartMeshes()
        {
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> partMeshes = NoPartMeshes();
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> lodPartMeshes = NoPartMeshes();

            PropLayer layer = PropLayer.PlacementLayer(OnePlacement(), partMeshes, 90f, 10f, lodPartMeshes, 40f);

            Assert.Same(partMeshes, layer.PartMeshes);
            Assert.Empty(layer.Meshes);
            Assert.Same(lodPartMeshes, layer.LodPartMeshes);
            Assert.True(layer.IsPlacement);
        }

        [Fact]
        public void PlacementLayer_WithHlod_PreservesPlacementsAndColliders()
        {
            IReadOnlyList<PropPlacement> placements = OnePlacement();
            PropLayer layer = PropLayer.PlacementLayer(placements, NoMeshes(), 90f, 15f, NoMeshes(), 60f,
                colliders: false);

            var source = new Dictionary<string, GltfMesh>();
            PropLayer hlod = layer.WithHlod(source, hlodDistance: 120f, weldCell: 2f);

            Assert.Same(placements, hlod.Placements);
            Assert.False(hlod.RegisterColliders);
            Assert.True(hlod.HasHlod);
            Assert.Equal(15f, hlod.FadeBandWidth);
            Assert.Equal(60f, hlod.LodDistance);
        }

        [Fact]
        public void ScatterLayer_RegisterCollidersDefaultsTrue()
        {
            PropLayer layer = PropLayer.ScatterLayer(new ScatterConfig(), NoMeshes(), 90f);
            Assert.True(layer.RegisterColliders);
            Assert.Null(layer.Placements);
            Assert.False(layer.IsPlacement);
        }
    }
}
