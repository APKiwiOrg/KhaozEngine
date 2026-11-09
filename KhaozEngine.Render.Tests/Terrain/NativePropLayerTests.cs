using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

public sealed class NativePropLayerTests
{
    static NativePropInstance Instance(string id = "native") => new(id, 17, "asset", Matrix4x4.Identity, new(32000, 0, -32000));
    static IReadOnlyList<NativePropLod> Tiers() => new[] { new NativePropLod(0, new[] { new MeshHandle(1), new MeshHandle(2) }),
        new NativePropLod(1, new[] { new MeshHandle(3) }), new NativePropLod(2, new[] { new MeshHandle(4) }) };
    static PropLayer Layer() => PropLayer.PlacementLayer(new[] { Instance() },
        new Dictionary<string, IReadOnlyList<NativePropLod>> { ["asset"] = Tiers() }, 100, new float[] { 10, 20 }, 4, castsShadows: false);

    [Fact]
    public void NativePlacementLayer_FreezesInventoryTiersAndThresholds()
    {
        var instances = new[] { Instance() };
        var tierList = Tiers().ToArray();
        var lods = new Dictionary<string, IReadOnlyList<NativePropLod>> { ["asset"] = tierList };
        var distances = new float[] { 10, 20 };
        var layer = PropLayer.PlacementLayer(instances, lods, 100, distances, 4, castsShadows: false);
        instances[0] = Instance("replacement"); tierList[0] = new(0, new[] { new MeshHandle(99) }); lods.Clear(); distances[0] = 99;
        Assert.True(layer.IsPlacement); Assert.Null(layer.Placements); Assert.Null(layer.PlacementSource);
        Assert.Equal("native", Assert.Single(layer.NativePlacements!).Id);
        Assert.Equal(new[] { 0, 1, 2 }, layer.NativeLods!["asset"].Select(l => l.Index));
        Assert.Equal(new[] { new MeshHandle(1), new MeshHandle(2) }, layer.NativeLods["asset"][0].Parts);
        Assert.Equal(new float[] { 10, 20 }, layer.LodDistances);
        Assert.Equal(4, layer.LodCrossfadeWidth); Assert.Equal(0, layer.LodDistance); Assert.False(layer.CastsShadows);
    }

    [Fact]
    public void NativeLayerCopies_RetainTheFrozenInventoryAndTiers()
    {
        var layer = Layer();
        var sources = new Dictionary<string, GltfMesh> { ["asset"] = new GltfMesh(Array.Empty<ModelVertex>(), Array.Empty<uint>()) };
        var copied = layer.WithHlod(sources, 30, 0, 10).WithIdentity("native-layer").WithLodCrossfade(6);
        Assert.Same(layer.NativePlacements, copied.NativePlacements); Assert.Same(layer.NativeLods, copied.NativeLods);
        Assert.Same(layer.LodDistances, copied.LodDistances);
        Assert.Equal("native-layer", copied.Identity); Assert.True(copied.HasHlod); Assert.False(copied.CastsShadows);
        Assert.Equal(6, copied.LodCrossfadeWidth);
        Assert.Same(copied.NativePlacements, copied.WithGeneration(null, null).NativePlacements);
    }

    [Theory]
    [InlineData(32010, 3, 0.5f)]
    [InlineData(32025, 1, 0)]
    public void SharedNativeLayerDraw_UsesEveryTierAndComplementaryCoverage(float focus, int expectedParts, float dissolve)
    {
        using var harness = new MotionTestScene(); harness.Scene.Begin();
        Assert.Equal(1, PropRenderer.DrawNative(harness.Scene, Layer(), new(focus, 0, -32000)));
        var draws = harness.Scene.QueuedInstancesForTests;
        Assert.Equal(expectedParts, draws.Count);
        Assert.All(draws, d => { Assert.Equal(dissolve, d.DissolveThreshold); Assert.False(d.CastsShadows); });
        if (expectedParts == 3) Assert.Equal(new[] { 0f, 0f, 1f }, draws.Select(d => d.DissolveComplement));
        else Assert.Equal(new MeshHandle(4), Assert.Single(draws).Mesh);
        Assert.All(draws, d => Assert.Equal(new RenderAnchor(32000, 0, -32000), d.Anchored!.Value.Anchor));
    }

    [Fact]
    public void NativeLayer_RefusesIncompleteTierProfilesAndTheLegacyAnalyticSink()
    {
        var lods = new Dictionary<string, IReadOnlyList<NativePropLod>> { ["asset"] = Tiers() };
        Assert.Throws<ArgumentException>(() => PropLayer.PlacementLayer(new[] { Instance() }, lods, 100, new float[] { 10 }));
        Assert.Throws<ArgumentException>(() => PropLayer.PlacementLayer(new[] { Instance() }, lods, 100, new float[] { 20, 10 }));
        Assert.Throws<ArgumentException>(() => PropLayerTopology.Validate(new[] { Layer() }, "layers"));
        var legacy = PropLayer.PlacementLayer(Array.Empty<PropPlacement>(), new Dictionary<string, MeshHandle>(), 100);
        Assert.Null(legacy.NativePlacements); Assert.Null(legacy.NativeLods); Assert.Empty(legacy.LodDistances);
        Assert.True(legacy.RegisterColliders); Assert.True(legacy.CastsShadows); Assert.Equal(0, legacy.LodDistance);
    }
}
