using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapPrefabIdentityTests
{
    [Theory]
    [InlineData("transform")]
    [InlineData("override")]
    [InlineData("binding")]
    [InlineData("reservation")]
    [InlineData("roof-group")]
    [InlineData("asset-version")]
    public void EveryAuthoredChange_MovesLoadedAndStoredIdentityTogether(string variant)
    {
        var a = PrefabFixtures.Create();
        var b = PrefabFixtures.Create(variant);
        string A(PrefabFixture f) => MapAuthoredIdentityV2.Compute(f.Document, f.Assets, PrefabFixtures.Options());
        Assert.NotEqual(A(a), A(b));
        Assert.Equal(A(b), MapAuthoredIdentityV2.Compute(PrefabFixtures.Stored(b.Document), b.Assets, PrefabFixtures.Options()));
    }

    [Fact]
    public void SixToSeven_MovesNativeIdentityEvenWithoutPrefabs()
    {
        var f = PrefabFixtures.Create("native-without-prefabs");
        Assert.NotEqual(PrefabFixtures.FormatSixRootDigest(f.Document), MapSurfaceSemantics.RootDigest(f.Document));
        Assert.Null(f.Document.Prefabs);
    }

    [Fact]
    public void BothIdentityPaths_RunTheSameBindingAndReservationChecks()
    {
        var f = PrefabFixtures.Create("active-transfer");
        MapPrefabIdentity.Require(f.Document, f.Assets);
        MapPrefabIdentity.RequireStored(PrefabFixtures.Stored(f.Document), f.Assets);
        var missing = PrefabFixtures.Create("missing-binding");
        var loaded = Assert.Throws<MapDocumentException>(() => MapPrefabIdentity.Require(missing.Document, missing.Assets));
        var stored = Assert.Throws<MapDocumentException>(() => MapPrefabIdentity.RequireStored(PrefabFixtures.Stored(missing.Document), missing.Assets));
        foreach (var error in new[] { loaded, stored })
        {
            Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
            Assert.Equal("a", error.InstanceId);
            Assert.Equal(new MapPrefabChildKey(MapPrefabChildKind.Record, "room"), error.Child);
            Assert.Equal("prefab binding", error.SubjectKind);
        }
        missing.Document.Prefabs = PrefabFixtures.Create().Document.Prefabs;
        MapPrefabIdentity.Require(missing.Document, missing.Assets);
        MapPrefabIdentity.RequireStored(PrefabFixtures.Stored(missing.Document), missing.Assets);
        var collision = PrefabFixtures.Create("standalone-collision");
        string before = MapDocumentFile.SaveText(collision.Document);
        var loadedCollision = Assert.Throws<MapDocumentException>(() => MapPrefabIdentity.Require(collision.Document, collision.Assets));
        var storedCollision = Assert.Throws<MapDocumentException>(() => MapPrefabIdentity.RequireStored(PrefabFixtures.Stored(collision.Document), collision.Assets));
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, loadedCollision.Reason);
        Assert.Equal(loadedCollision.Reason, storedCollision.Reason);
        Assert.Equal(("a/item", "a/item", MapPrefabChildKind.Placement, "a", "standalone owner"),
            (loadedCollision.EntityId, loadedCollision.WorldId, loadedCollision.ChildKind, loadedCollision.InstanceId, loadedCollision.SubjectKind));
        Assert.Equal(new MapPrefabChildKey(MapPrefabChildKind.Placement, "item"), loadedCollision.Child);
        Assert.Equal((loadedCollision.EntityId, loadedCollision.WorldId, loadedCollision.ChildKind, loadedCollision.Child, loadedCollision.InstanceId, loadedCollision.SubjectKind),
            (storedCollision.EntityId, storedCollision.WorldId, storedCollision.ChildKind, storedCollision.Child, storedCollision.InstanceId, storedCollision.SubjectKind));
        Assert.Equal(before, MapDocumentFile.SaveText(collision.Document));
        collision.Document.Placements.Single().Id = "standalone-free";
        MapPrefabIdentity.Require(collision.Document, collision.Assets);
        MapPrefabIdentity.RequireStored(PrefabFixtures.Stored(collision.Document), collision.Assets);
    }

    [Fact]
    public void BothEntryPoints_RefuseNumericHighWaterAndClosureFaults()
    {
        var lowered = PrefabFixtures.Create();
        lowered.Document.NumericIdHighWaterMark = 100;
        Refuses(lowered, lowered.Assets, MapDocumentRefusal.NumericIdHighWaterExceeded, error =>
        {
            Assert.Equal(100, error.HighWaterMark);
            Assert.True(error.NumericId > error.HighWaterMark);
            Assert.False(string.IsNullOrEmpty(error.EntityId));
        });
        lowered.Document.NumericIdHighWaterMark = PrefabFixtures.Create().Document.NumericIdHighWaterMark;
        MapPrefabIdentity.Require(lowered.Document, lowered.Assets);
        MapPrefabIdentity.RequireStored(PrefabFixtures.Stored(lowered.Document), lowered.Assets);
        var duplicate = PrefabFixtures.Create("numeric-duplicate");
        Refuses(duplicate, duplicate.Assets, MapDocumentRefusal.IdentityDuplicate, error =>
        {
            Assert.Equal("standalone", error.EntityId);
            Assert.Equal("placement", error.SubjectKind);
            Assert.Equal(duplicate.Document.Placements.Single().NumericId, error.NumericId);
        });
        duplicate.Document.Placements.Single().NumericId = ++duplicate.Document.NumericIdHighWaterMark;
        MapPrefabIdentity.Require(duplicate.Document, duplicate.Assets);
        MapPrefabIdentity.RequireStored(PrefabFixtures.Stored(duplicate.Document), duplicate.Assets);
        var f = PrefabFixtures.Create();
        var other = PrefabFixtures.Create("asset-version").Assets;
        var loaded = Assert.Throws<MapDocumentException>(() => MapPrefabIdentity.Require(f.Document, other));
        var stored = Assert.Throws<MapDocumentException>(() => MapPrefabIdentity.RequireStored(PrefabFixtures.Stored(f.Document), other));
        foreach (var error in new[] { loaded, stored })
        {
            Assert.Equal(MapDocumentRefusal.DocumentAssetRootsMismatch, error.Reason);
            Assert.Equal(f.Document.NativeAssets.Select(r => r.Id), error.ResourceIds);
            Assert.Equal(other.Roots.Select(r => r.Id), error.ExpectedResourceIds);
        }
        MapPrefabIdentity.Require(f.Document, f.Assets);
        MapPrefabIdentity.RequireStored(PrefabFixtures.Stored(f.Document), f.Assets);
    }

    // A document the stored writer itself refuses cannot reach RequireStored, so its refusal names the same fault.
    static void Refuses(PrefabFixture f, KhaozEngine.MapDoc.Assets.MapAssetClosure assets, MapDocumentRefusal reason,
        Action<MapDocumentException> identifiers)
    {
        var loaded = Assert.Throws<MapDocumentException>(() => MapPrefabIdentity.Require(f.Document, assets));
        var stored = Assert.Throws<MapDocumentException>(() => MapPrefabIdentity.RequireStored(PrefabFixtures.Stored(f.Document), assets));
        foreach (var error in new[] { loaded, stored })
        {
            Assert.Equal(reason, error.Reason);
            identifiers(error);
        }
        Assert.Equal((loaded.EntityId, loaded.SubjectKind, loaded.NumericId, loaded.HighWaterMark),
            (stored.EntityId, stored.SubjectKind, stored.NumericId, stored.HighWaterMark));
        Assert.Equal(loaded.Findings?.Select(f => (f.Reason, f.EntityId, f.NumericId, f.HighWaterMark)),
            stored.Findings?.Select(f => (f.Reason, f.EntityId, f.NumericId, f.HighWaterMark)));
    }

    [Fact]
    public void ChildDigest_IsCanonicalAndCoversScope()
    {
        var asset = PrefabFixtures.Create().Assets.GetPrefab("hut");
        var portal = asset.Children.Single(c => c.Key.Key == "room-portal");
        var reordered = portal with { References = portal.References.Reverse().ToArray() };
        Assert.Equal(MapPrefabIdentity.ChildDigest(portal), MapPrefabIdentity.ChildDigest(reordered));
        var world = portal with
        {
            References = portal.References.Select(s => s.Path.StartsWith("/value/bandTop/", System.StringComparison.Ordinal)
                ? s with { Target = s.Target with { Scope = MapPrefabReferenceScope.World } } : s).ToArray(),
        };
        Assert.NotEqual(MapPrefabIdentity.ChildDigest(portal), MapPrefabIdentity.ChildDigest(world));
    }
}
