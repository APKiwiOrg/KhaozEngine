using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

public sealed class PropLayerInteriorCutTests
{
    [Fact]
    public void LayerCopiesKeepTheOptIn()
    {
        var meshes = new Dictionary<string, MeshHandle> { ["tree"] = new(1) };
        var parts = new Dictionary<string, IReadOnlyList<MeshHandle>> { ["tree"] = new[] { new MeshHandle(1) } };
        var scatter = new ScatterConfig();
        var companion = new CompanionConfig();
        PropPlacement[] placements = [];
        PropLayer[] layers =
        [
            PropLayer.ScatterLayer(scatter, meshes, 192f, cutInsideInteriors: true),
            PropLayer.ScatterLayer(scatter, parts, 192f, cutInsideInteriors: true),
            PropLayer.CompanionLayer(0, companion, meshes, 192f, cutInsideInteriors: true),
            PropLayer.CompanionLayer(0, companion, parts, 192f, cutInsideInteriors: true),
            PropLayer.PlacementLayer(placements, meshes, 192f, cutInsideInteriors: true),
            PropLayer.PlacementLayer(placements, parts, 192f, cutInsideInteriors: true),
            PropLayer.PlacementLayer(new EmptyPlacementSource(), meshes, 192f, cutInsideInteriors: true),
            PropLayer.PlacementLayer(new EmptyPlacementSource(), parts, 192f, cutInsideInteriors: true),
        ];
        foreach (PropLayer layer in layers)
        {
            Assert.True(layer.CutsInsideInteriors);
            Assert.True(layer.WithIdentity("trees").CutsInsideInteriors);
            Assert.True(layer.WithLodCrossfade(8f).CutsInsideInteriors);
            Assert.True(layer.WithHlod(new Dictionary<string, GltfMesh>(), 192f, 1f).CutsInsideInteriors);
            Assert.True(layer.WithGeneration(scatter, companion).CutsInsideInteriors);
        }
        Assert.False(PropLayer.ScatterLayer(scatter, meshes, 192f).CutsInsideInteriors);
        Assert.False(PropLayerTopology.OnlyGenerationDiffers(
            [PropLayer.ScatterLayer(scatter, meshes, 192f)], [layers[0]]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthoredLodIsCutButMergedAndFoliageAreNot(bool multipart)
    {
        using var rig = new MotionTestScene();
        Scene3D scene = rig.Scene;
        scene.InteriorCut = InteriorHeightFieldBuilder.Build([new InteriorVolume
        {
            Outline = [new(19, -2), new(22, -2), new(22, 2), new(19, 2)], FloorY = -1, CeilingY = 3,
        }], new InteriorWindow(new Vector2(-32), .25f, 256), InteriorEncoding.For(-1, 3));
        MeshHandle full = scene.LoadMesh(MeshPrimitives.Box());
        MeshHandle lod = scene.LoadMesh(MeshPrimitives.Box());
        var meshes = new Dictionary<string, MeshHandle> { ["tree"] = full };
        var lods = new Dictionary<string, MeshHandle> { ["tree"] = lod };
        var parts = new Dictionary<string, IReadOnlyList<MeshHandle>> { ["tree"] = new[] { full, full } };
        var lodParts = new Dictionary<string, IReadOnlyList<MeshHandle>> { ["tree"] = new[] { lod, lod } };
        PropPlacement[] placements = [new("tree", 20f, 0f, 0f, 1f, 0f, 0)];
        PropLayer layer = multipart
            ? PropLayer.PlacementLayer(placements, parts, 300f, lodPartMeshes: lodParts, lodDistance: 10f,
                cutInsideInteriors: true)
            : PropLayer.PlacementLayer(placements, meshes, 300f, lodMeshes: lods, lodDistance: 10f,
                cutInsideInteriors: true);
        layer = layer.WithHlod(new Dictionary<string, GltfMesh> { ["tree"] = MeshPrimitives.Box() }, 100f, 0f);
        using var renderer = new PropClusterRenderer(scene);
        var key = new PropClusterKey("trees", 0, 0, 0);
        renderer.Apply(key, renderer.BuildCpu(new PropClusterBuildRequest(key, 1,
            new RectArea(-32f, -32f, 32f, 32f), layer, placements)));
        scene.Begin();
        renderer.Draw(Vector3.Zero);
        Assert.All(scene.QueuedInstancesForTests, draw => Assert.True(draw.CutInsideInteriors));
        Assert.Equal(multipart ? 2 : 1, scene.QueuedInstancesForTests.Count);
        Assert.All(scene.QueuedInstancesForTests, draw => Assert.Equal(lod, draw.Mesh));
        scene.Begin();
        renderer.Draw(new Vector3(150f, 0f, 0f));
        Assert.False(Assert.Single(scene.QueuedInstancesForTests).CutInsideInteriors);
        scene.Begin();
        PropRenderer.Queue(new SceneInstances(), placements, meshes, Vector3.Zero, 100f);
        scene.DrawShadowOnlyProps(placements, parts, Vector3.Zero, 100f);
        Assert.All(scene.QueuedInstancesForTests, draw => Assert.False(draw.CutInsideInteriors));
    }

    [Fact]
    public void TerminalOverloadsOptInAndConvenienceOverloadsStayUncut()
    {
        using var rig = new MotionTestScene();
        rig.Scene.InteriorCut = InteriorCutShadowTests.Field();
        var single = new Dictionary<string, MeshHandle> { ["tree"] = rig.Scene.LoadMesh(MeshPrimitives.Box()) };
        var parts = new Dictionary<string, IReadOnlyList<MeshHandle>>
        { ["tree"] = new[] { rig.Scene.LoadMesh(MeshPrimitives.Box()) } };
        PropPlacement[] placements = [new("tree", 0f, 0f, 0f, 1f, 0f, 0)];
        rig.Scene.Begin();
        rig.Scene.DrawProps(placements, single, Vector3.Zero, 10f, 2f, layerIdentity: "trees", cutInsideInteriors: true);
        rig.Scene.DrawProps(placements, parts, Vector3.Zero, 10f, 2f, layerIdentity: "trees", cutInsideInteriors: true);
        Assert.Equal(2, rig.Scene.QueuedInstancesForTests.Count);
        Assert.All(rig.Scene.QueuedInstancesForTests, draw => Assert.True(draw.CutInsideInteriors));
        rig.Scene.Begin();
        rig.Scene.DrawProps(placements, single, Vector3.Zero, 10f);
        rig.Scene.DrawProps(placements, single, Vector3.Zero, 10f, 2f, null, 0f, null, 0f, 0f, true, null);
        rig.Scene.DrawProps(placements, parts, Vector3.Zero, 10f);
        rig.Scene.DrawProps(placements, parts, Vector3.Zero, 10f, 2f, null, 0f, null, 0f, 0f, true, null);
        Assert.Equal(4, rig.Scene.QueuedInstancesForTests.Count);
        Assert.All(rig.Scene.QueuedInstancesForTests, draw => Assert.False(draw.CutInsideInteriors));
    }

    sealed class EmptyPlacementSource : IPlacementSource
    {
        public void PlacementsIn(RectArea area, List<PropPlacement> into) { }
    }

}
