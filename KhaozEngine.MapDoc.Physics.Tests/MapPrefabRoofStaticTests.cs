using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapPrefabRoofStaticTests
{
    [Fact]
    public void HidingNeverChangesStaticsOrInteractionGeometry()
    {
        var world = PrefabPhysicsFixtures.Build(PrefabFixtures.Create("linked-buildings"));
        var r = world.Document.EffectiveResources!;
        var statics = world.Statics.Select(x => (x.OwnerId, x.Digest, x.Bounds, x.Position, x.Orientation)).ToArray();
        var interactions = world.Placements.Select(x => (x.PlacementId, x.Digest, x.Envelope.Bounds)).ToArray();
        Assert.NotEmpty(statics);
        using var physics = new BepuPhysicsWorld();
        using var registration = MapPhysicsRegistration.Register(world, physics);
        int count = registration.Handles.Count;
        Assert.Equal(statics.Length, count);
        foreach (var mode in Enum.GetValues<MapRoofMode>())
        {
            var result = MapRoofPolicy.Evaluate(r, world.Surfaces, PrefabFixtures.LinkedRoomPoint(r), mode);
            Assert.Equal(3, result.Roofs.Count);
            Assert.Equal(statics, world.Statics.Select(x => (x.OwnerId, x.Digest, x.Bounds, x.Position, x.Orientation)));
            Assert.Equal(interactions, world.Placements.Select(x => (x.PlacementId, x.Digest, x.Envelope.Bounds)));
            Assert.Equal(count, registration.Handles.Count);
        }
        Assert.Equal(2, world.RoofLinks.Count);
        Assert.Equal(2, world.RoofGroups.Count);
    }

    [Fact]
    public void LightBoundsIncludeScaledRadiusAndOwnerOffsetOnce()
    {
        var f = PrefabFixtures.Create("two-leaf-lamps");
        var world = PrefabPhysicsFixtures.Build(f);
        Assert.Equal(new[] { "light-a", "light-b" }, world.Lights.Select(x => x.Id));
        var entries = MapResidencyOwnership.BuildAnnotations(world, new(world.TileSize, 1, 4, Vector2.Zero));
        Assert.Equal(new[] { "light:light-a", "light:light-b" }, entries.Select(x => x.OwnerId));
        double radius = 2.4f;
        double y = 6.2f;
        Assert.Equal(new MapBox3(10.25 - radius, y - radius, -3.125 - radius, 10.25 + radius, y + radius, -3.125 + radius), entries[0].WorldBounds);
        Assert.NotEmpty(entries[0].Membership);
        Assert.True(world.Bounds.MaxY >= y + radius);
        var reload = PrefabPhysicsFixtures.Build(new(MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document)), f.Assets));
        Assert.Equal(world.Lights, reload.Lights);
        Assert.Equal(world.AnnotationDigest, reload.AnnotationDigest);
        Assert.Equal(world.BuildHash, reload.BuildHash);

    }

    [Fact]
    public void MarkerDataAndResidency_PreserveFarExactPositionAndInstance()
    {
        var world = PrefabPhysicsFixtures.Build(PrefabFixtures.Transformed(0.371f, 1.2f, new(30000.25f, -450, -29999.75f)));
        var marker = Assert.Single(world.Markers);
        Assert.Equal("a", marker.InstanceId);
        Assert.Equal("entrance", marker.Role);
        Assert.Throws<NotSupportedException>(() => ((System.Collections.Generic.IList<string>)marker.Tags)[0] = "changed");
        Assert.Throws<NotSupportedException>(() => ((System.Collections.Generic.IList<MapRoofLinkDoc>)world.RoofLinks)[0] = world.RoofLinks[0] with { AboveY = 0 });
        var entry = MapResidencyOwnership.BuildAnnotations(world, new(world.TileSize, 1, 4, Vector2.Zero)).Single(x => x.OwnerId == "marker:a/entry");
        Assert.True(entry.WorldBounds.MinX <= marker.Position.X.ToDouble());
        Assert.True(entry.WorldBounds.MaxX >= marker.Position.X.ToDouble());
        Assert.NotEmpty(entry.Membership);
    }

    [Fact]
    public void AnnotationEdits_ChangeDigestAndInvalidateOwnResidency()
    {
        var f = PrefabFixtures.TwoLeafLamps(true);
        var before = PrefabPhysicsFixtures.Build(f);
        var instance = f.Document.Prefabs!.Instances.Single();
        var asset = f.Assets.GetPrefab("hut");
        var child = asset.Children.OfType<MapPrefabLightChild>().Single();
        f.Document.Prefabs = new(1, 1, new[] { instance with { Overrides = new MapPrefabOverride[]
        { new MapPrefabReplaceOverride(child.Key, child with { Value = child.Value with { Radius = 5 } }) } } }, []);
        var after = PrefabPhysicsFixtures.Build(f);
        Assert.NotEqual(before.AnnotationDigest, after.AnnotationDigest);
        Assert.NotEqual(before.BuildHash, after.BuildHash);
        var effects = new MapNativeEditEffects(new(-2, -1, -2, 2, 3, 2), new(-5, -4, -5, 5, 6, 5), [], [], [], [], MapNativeInvalidation.Residency);
        var affected = MapResidencyOwnership.Affected(before, after, new(before.TileSize, 1, 4, Vector2.Zero), effects);
        Assert.Contains("light:light-extra", affected.Owners);
        Assert.NotEmpty(affected.StorageTiles);
    }
}
