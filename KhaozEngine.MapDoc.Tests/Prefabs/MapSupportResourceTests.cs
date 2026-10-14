using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public class MapSupportResourceTests
{
    [Fact]
    public void DryVariant_PreservesGeometryIdsAndDefaultWater()
    {
        var fixture = PrefabFixtures.Create("all-resources");
        var before = PrefabFixtures.Effective(fixture);
        var dry = PrefabFixtures.Effective(PrefabFixtures.DryAllResources());
        Assert.Single(before.Water!.Bodies);
        Assert.Single(before.Water.Media);
        Assert.Null(dry.Water);
        Assert.Equal(before.Placements.Select(p => (p.PlacementId, p.NumericId, p.Transform)),
            dry.Placements.Select(p => (p.PlacementId, p.NumericId, p.Transform)));
        Assert.Equal(before.Surfaces.Refs, dry.Surfaces.Refs);
        Assert.Equal(before.Surfaces.Patches.Select(p => (p.Key, MapSurfaceSemantics.PatchDigest(p.Value))),
            dry.Surfaces.Patches.Select(p => (p.Key, MapSurfaceSemantics.PatchDigest(p.Value))));
        Assert.Single(PrefabFixtures.Effective(fixture).Water!.Bodies);
    }

    [Theory]
    [InlineData(MapSurfaceRole.Ceiling)]
    [InlineData(MapSurfaceRole.PaintOverride)]
    public void ResourceRejectsNonSupportRoles(MapSurfaceRole role)
    {
        var resource = new MapSupportResource(1, PrefabFixtures.FloorResource(role), null,
            PrefabFixtures.Slots(PrefabFixtures.Surf("/patches/0/key/surfaceId", "floor")));
        Assert.Throws<MapDocumentException>(() => MapSupportResourceFile.Write(resource));
    }

    [Fact]
    public void ResourceRejectsInvalidMetreFrame()
    {
        var set = PrefabFixtures.FloorResource();
        set.Refs[0] = set.Refs[0] with { Frame = set.Refs[0].Frame with { RowDirection = (MapRowDirection)99 } };
        var resource = new MapSupportResource(1, set, null,
            PrefabFixtures.Slots(PrefabFixtures.Surf("/patches/0/key/surfaceId", "floor")));
        Assert.Throws<MapDocumentException>(() => MapSupportResourceFile.Write(resource));
    }

    [Fact]
    public void ImplicitFloorBindingsAndRoomAnchorsPersistWithoutAllocation()
    {
        var f = PrefabFixtures.Create("support-resource");
        long highWater = f.Document.NumericIdHighWaterMark;
        var reloaded = MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document));
        var resources = MapPrefabResolver.Expand(reloaded, f.Assets);
        Assert.Contains(resources.Surfaces.Refs, s => s.Id == PrefabFixtures.SupportFloorId);
        var room = resources.Surfaces.AllRecords().Single(r => r.Id == "a/room");
        Assert.Contains(resources.Surfaces.Patches[new(PrefabFixtures.SupportFloorId, 0, 0)].Records, r => r == room);
        Assert.Equal(highWater, reloaded.NumericIdHighWaterMark);
        Assert.Null(PrefabFixtures.LiveBindings(reloaded).Single(b => b.WorldId == PrefabFixtures.SupportFloorId).NumericId);
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, -450)]
    public void ImplicitSupportMissingY_ResolvesInMapDocBeforePhysics(bool far, float expectedY)
    {
        var f = PrefabFixtures.SupportAt(far);
        long highWater = f.Document.NumericIdHighWaterMark;
        var resolution = MapResolverV2.Resolve(f.Document, f.Assets, MapDocumentSurfaceSource.Capture(f.Document), PrefabFixtures.Options());
        Assert.Contains(resolution.Document.EffectiveResources!.Surfaces.Refs, s => s.Id == PrefabFixtures.SupportFloorId);
        var support = Assert.Single(resolution.Supports);
        Assert.Equal(MapSupportStatus.Supported, support.Support.Status);
        Assert.NotNull(support.Support.Face);
        Assert.Equal(expectedY, support.Support.WorldY);
        Assert.Equal(expectedY, resolution.Document.Placements.Single(p => p.PlacementId == "low-item").Transform.Position.Y);
        Assert.Equal(highWater, f.Document.NumericIdHighWaterMark);
    }
}
