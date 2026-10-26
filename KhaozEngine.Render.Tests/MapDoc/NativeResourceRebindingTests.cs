using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeResourceRebindingTests
{
    [Theory]
    [InlineData("point-capacity", false)]
    [InlineData("point-capacity", true)]
    [InlineData("shadow-atlas", false)]
    [InlineData("shadow-atlas", true)]
    public void Rebinding_TransfersPublishedMeshAndGroundDisposalOwnership(string replacement, bool leaveLive)
    {
        FakeResourceSet[] resourceSets;
        using (var host = new NativeUploadTestScene())
        {
            var scene = host.Scene;
            var factory = (FakeGpuResourceFactory)host.Device.Factory;
            int texturesBefore = factory.Textures.Count;
            var mesh = NativeResourceUploadTests.Publish(scene, new(NativeResourceUploadTests.Triangle(),
                new(new(new byte[] { 255, 0, 0, 255 }, 1, 1), null, null, 0), null, default));
            var ground = NativeResourceUploadTests.Publish(scene, Ground());
            var textures = new HashSet<IGpuTexture>(factory.Textures.Skip(texturesBefore));
            var original = factory.ResourceSets.Where(UsesResourceTexture).ToArray();
            Assert.Equal(3, original.Length); // Rigid material, outline material and tile-ground material.

            if (replacement == "point-capacity")
            {
                scene.Begin();
                for (int i = 0; i < 17; i++) scene.AddLight(new(i, 2, 0), Color.White, 6, 1);
                host.Render();
            }
            else Assert.True(scene.ReplaceShadowLayout(1024, 2));

            resourceSets = factory.ResourceSets.Where(UsesResourceTexture).ToArray();
            Assert.True(resourceSets.Length > original.Length);
            Assert.All(resourceSets.Except(original), set => Assert.Equal(0, set.DisposeCount));
            Assert.All(original, set => Assert.InRange(set.DisposeCount, 0, 1));
            // Shadow replacement retains old bindings behind completion. Point growth releases its old sets directly.
            Assert.Equal(replacement == "shadow-atlas" ? 0 : 2, original.Count(set => set.DisposeCount == 1));

            if (!leaveLive)
            {
                scene.ReleaseResource(mesh);
                scene.ReleaseResource(ground);
                scene.Begin(); scene.Begin(); scene.Begin(); scene.Begin();
                Assert.All(resourceSets, set => Assert.Equal(1, set.DisposeCount));
            }

            bool UsesResourceTexture(FakeResourceSet set) => set.Resources.OfType<IGpuTexture>().Any(textures.Contains);
        }
        Assert.All(resourceSets, set => Assert.Equal(1, set.DisposeCount));
    }

    [Fact]
    public void GroundPublication_ReusesOnlyResourceOwnedSlotsAndPreservesLegacyHandles()
    {
        using var host = new NativeUploadTestScene();
        var scene = host.Scene;
        var layers = new[] { new TileGroundLayerImage { AlbedoRgba = new byte[] { 255, 255, 255, 255 } } };
        var legacy = scene.LoadTileGroundMaterial(1, 1, layers);
        var table = (IList)typeof(Scene3D).GetField("_tileGroundMaterials", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene)!;
        object? legacyEntry = table[legacy.ListIndex];
        var held = NativeResourceUploadTests.Publish(scene, Ground());
        for (int i = 0; i < 8; i++)
        {
            var temporary = NativeResourceUploadTests.Publish(scene, Ground());
            Assert.InRange(table.Count, 0, 3);
            Assert.Same(legacyEntry, table[legacy.ListIndex]);
            Assert.Equal(3, scene.LiveTileGroundMaterialCount);
            scene.ReleaseResource(temporary);
            scene.Begin(); scene.Begin(); scene.Begin(); scene.Begin();
        }
        scene.ReleaseResource(held);
        scene.UnloadTileGroundMaterial(legacy);
        var next = NativeResourceUploadTests.Publish(scene, Ground());
        Assert.Null(table[legacy.ListIndex]);
        Assert.InRange(table.Count, 0, 3);
        scene.ReleaseResource(next);
    }

    static SceneUploadData Ground() => new(NativeResourceUploadTests.Triangle(), null, null, default,
        new SceneGroundMaterialData(1, 1, new[] { new TileGroundLayerImage { AlbedoRgba = new byte[] { 40, 80, 120, 255 } } }));
}
