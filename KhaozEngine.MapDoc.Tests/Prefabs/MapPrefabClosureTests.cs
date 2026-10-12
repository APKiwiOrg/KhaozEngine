using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabClosureTests
{
    [Fact]
    public void ChangedBytesRequireNewDigest_AndCyclesRefuse()
    {
        Assert.Throws<MapDocumentException>(() => PrefabFixtures.LoadAssets(PrefabFixtures.AssetBytes("changed"), false));
        Assert.NotEqual(PrefabFixtures.Create().Assets.Hash,
            PrefabFixtures.LoadAssets(PrefabFixtures.AssetBytes("changed"), true).Hash);
        Assert.Throws<MapDocumentException>(() => PrefabFixtures.Create("cyclic-closure"));
    }

    [Theory]
    [InlineData("material")]
    [InlineData("collider")]
    [InlineData("light")]
    [InlineData("surface")]
    [InlineData("undeclared")]
    [InlineData("unknown-asset")]
    public void UnknownPrefabMaterialColliderLightOrSurface_RefusesBeforeBuild(string variant) =>
        Assert.Throws<MapDocumentException>(() => PrefabFixtures.Create("wrong-" + variant));

    [Fact]
    public void ClosureSnapshot_IsVerifiedOnceAndDetachedFromReturnedPayload()
    {
        var f = PrefabFixtures.Create();
        var payload = f.Assets.GetPrefab("hut");
        var placement = payload.Children.OfType<MapPrefabPlacementChild>().First();
        placement.Value.Tags.Add("changed");
        Assert.DoesNotContain("changed", f.Assets.GetPrefab("hut").Children.OfType<MapPrefabPlacementChild>().First().Value.Tags);
        Assert.Empty(MapPrefabValidation.Validate(f.Document, f.Assets));
    }

    [Fact]
    public void RemovedPreviousOverride_RetainsItsClosureDependencies()
    {
        var f = PrefabFixtures.Create("roomless");
        var instance = f.Document.Prefabs!.Instances.Single();
        var key = instance.Bindings.Entries.Single().Child;
        instance = instance with
        {
            Overrides = new MapPrefabOverride[]
        { new MapPrefabRemoveOverride(key, new MapPrefabPlacementOverride(key, null, "missing", null)) }
        };
        f.Document.Prefabs = new(1, 1, new[] { instance }, []);
        Assert.Throws<MapDocumentException>(() => MapBoundDocumentValidation.Validate(f.Document, f.Assets));
    }

    [Fact]
    public void RemovedAddedChild_RetainsItsCompleteBindingAndPreviousPayload()
    {
        var f = PrefabFixtures.Create("roomless");
        var instance = f.Document.Prefabs!.Instances.Single();
        var key = new MapPrefabChildKey(MapPrefabChildKind.Placement, "second");
        var child = new MapPrefabPlacementChild(key, new MapPlacement { Id = "second", Kind = "item", AssetId = "item", Y = 0, Scale = 1 });
        instance = instance with
        {
            Bindings = new(instance.Bindings.Entries.Append(new MapPrefabBinding(key, "a/second", 102, true)).ToArray()),
            Overrides = new MapPrefabOverride[] { new MapPrefabRemoveOverride(key, new MapPrefabAddOverride(key, child)) },
        };
        f.Document.NumericIdHighWaterMark = 102;
        f.Document.Prefabs = new(1, 1, new[] { instance }, []);
        Assert.Empty(MapPrefabValidation.Validate(f.Document, f.Assets));
        var copy = MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document));
        Assert.IsType<MapPrefabAddOverride>(Assert.IsType<MapPrefabRemoveOverride>(copy.Prefabs!.Instances.Single().Overrides.Single()).Previous);
        Assert.Contains(copy.Prefabs.Instances.Single().Bindings.Entries, b => b.Child == key && b.Removed && b.NumericId == 102);
    }

    [Fact]
    public void LocalTransformOverride_RequiresMatchingLocalGeometry()
    {
        var f = PrefabFixtures.Create();
        var instance = f.Document.Prefabs!.Instances.Single();
        instance = instance with
        {
            Overrides = new MapPrefabOverride[]
        { new MapPrefabTransformOverride(new(MapPrefabChildKind.Record, "room"), new(new System.Numerics.Vector3(1, 0, 0), 0, 1)) }
        };
        f.Document.Prefabs = new(1, 1, new[] { instance }, []);
        Assert.NotEmpty(MapPrefabValidation.Validate(f.Document, f.Assets));
    }

    [Fact]
    public void Clone_DetachesMutableOverridesAndCollections()
    {
        var f = PrefabFixtures.Create("roomless");
        var child = MapPrefabFile.Read(PrefabFixtures.AssetBytes("roomless"), "hut").Children.Single();
        var instance = f.Document.Prefabs!.Instances.Single() with { Overrides = new MapPrefabOverride[] { new MapPrefabReplaceOverride(child.Key, child) } };
        var original = new MapPrefabSet(1, 1, new[] { instance }, []);
        var clone = original.Clone();
        var replacement = (MapPrefabPlacementChild)((MapPrefabReplaceOverride)clone.Instances.Single().Overrides.Single()).Value;
        replacement.Value.Tags.Add("changed");
        Assert.NotEqual(original.Digest(), clone.Digest());
    }
}
