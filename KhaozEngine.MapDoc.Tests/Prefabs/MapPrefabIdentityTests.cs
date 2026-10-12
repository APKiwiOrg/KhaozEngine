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
        Assert.Equal(loaded.Message, stored.Message);
        var collision = PrefabFixtures.Create("standalone-collision");
        Assert.Equal(Assert.Throws<MapDocumentException>(() => MapPrefabIdentity.Require(collision.Document, collision.Assets)).Message,
            Assert.Throws<MapDocumentException>(() => MapPrefabIdentity.RequireStored(PrefabFixtures.Stored(collision.Document), collision.Assets)).Message);
    }

    [Fact]
    public void BothEntryPoints_RefuseNumericHighWaterAndClosureFaults()
    {
        var lowered = PrefabFixtures.Create();
        lowered.Document.NumericIdHighWaterMark = 100;
        Refuses(lowered, lowered.Assets, "exceeds numericIdHighWaterMark");
        var duplicate = PrefabFixtures.Create("numeric-duplicate");
        Refuses(duplicate, duplicate.Assets, "duplicate numericId");
        var f = PrefabFixtures.Create();
        var other = PrefabFixtures.Create("asset-version").Assets;
        var loaded = Assert.Throws<MapDocumentException>(() => MapPrefabIdentity.Require(f.Document, other));
        var stored = Assert.Throws<MapDocumentException>(() => MapPrefabIdentity.RequireStored(PrefabFixtures.Stored(f.Document), other));
        Assert.Equal(loaded.Message, stored.Message);
    }

    // A document the stored writer itself refuses cannot reach RequireStored, so its refusal names the same fault.
    static void Refuses(PrefabFixture f, KhaozEngine.MapDoc.Assets.MapAssetClosure assets, string fault)
    {
        Assert.Contains(fault, Assert.Throws<MapDocumentException>(() => MapPrefabIdentity.Require(f.Document, assets)).Message);
        Assert.Contains(fault, Assert.Throws<MapDocumentException>(() => MapPrefabIdentity.RequireStored(PrefabFixtures.Stored(f.Document), assets)).Message);
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
