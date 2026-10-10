using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativePlacementEffectsTests
{
    const MapNativeInvalidation Spatial = MapNativeInvalidation.Placements | MapNativeInvalidation.Physics |
        MapNativeInvalidation.Nav | MapNativeInvalidation.Residency;

    [Fact]
    public void Move_WithProviderBound_ReportsColliderAndEnvelopeBeforeAndAfter()
    {
        var f = new NativePlacementEffectsFixture(bindProvider: true);
        MapBox3 before = NativePlacementEffectsFixture.ColliderAndEnvelope(f.Build(), "crate-1");
        f.Editor.Execute(new MovePlacementCommand("crate-1", 100f, 10f, 0f));
        MapBox3 after = NativePlacementEffectsFixture.ColliderAndEnvelope(f.Build(), "crate-1");

        MapNativeEditEffects effects = f.Editor.LastNativeEffects!;
        Assert.Equal(Spatial, effects.Invalidates);
        Assert.Equal(before, effects.OldBounds);
        Assert.Equal(after, effects.NewBounds);
        // The 0.5 m collider sits inside the 1 m envelope, so the envelope decides the top.
        Assert.Equal((9.5, 0d, 10.5, 1d), (before.MinX, before.MinY, before.MaxX, before.MaxY));
        Assert.Equal((99.5, 0d, 100.5, 1d), (after.MinX, after.MinY, after.MaxX, after.MaxY));
    }

    [Fact]
    public void Move_AcrossANavSeam_ListsTheOldAndTheNewTile()
    {
        var f = new NativePlacementEffectsFixture(bindProvider: true);
        f.Editor.Execute(new MovePlacementCommand("crate-1", 100f, 10f, 0f));
        var tiles = MapNavTiling.AffectedTiles(f.Build(), NativePlacementEffectsFixture.Grids,
            NativePlacementEffectsFixture.NavOptions, f.Editor.LastNativeEffects!);
        Assert.Equal(new[] { new MapNavTileCoord(0, 0), new MapNavTileCoord(1, 0) }, tiles);
    }

    [Fact]
    public void AddAndDelete_ReportOnlyTheSideThatHasThePlacement()
    {
        var f = new NativePlacementEffectsFixture(bindProvider: true);
        f.Editor.Execute(new AddPlacementCommand(NativePlacementEffectsFixture.Crate("crate-2", 30f, -20f)));
        MapNativeEditEffects added = f.Editor.LastNativeEffects!;
        MapBox3 placed = NativePlacementEffectsFixture.ColliderAndEnvelope(f.Build(), "crate-2");
        Assert.Equal((Spatial, (MapBox3?)null, (MapBox3?)placed), (added.Invalidates, added.OldBounds, added.NewBounds));

        f.Editor.Execute(new RemovePlacementCommand("crate-2"));
        MapNativeEditEffects removed = f.Editor.LastNativeEffects!;
        Assert.Equal((Spatial, (MapBox3?)placed, (MapBox3?)null), (removed.Invalidates, removed.OldBounds, removed.NewBounds));
    }

    [Fact]
    public void RenameOnly_ReportsNoPhysicsNavOrResidency()
    {
        var f = new NativePlacementEffectsFixture(bindProvider: true);
        f.Editor.Execute(new RenamePlacementCommand("crate-1", "Front crate"));
        MapNativeEditEffects effects = f.Editor.LastNativeEffects!;
        Assert.Equal("Front crate", f.Document.Placements.Single().DisplayName);
        Assert.Equal((MapNativeInvalidation.Placements, (MapBox3?)null, (MapBox3?)null),
            (effects.Invalidates, effects.OldBounds, effects.NewBounds));
    }

    [Fact]
    public void MeshOnlyPlacement_MovesAndReportsPlacementsOnly()
    {
        var f = new NativePlacementEffectsFixture(bindProvider: true, withMeshOnly: true);
        f.Editor.Execute(new MovePlacementCommand("marker-1", -40f, -10f, 0f));
        MapNativeEditEffects effects = f.Editor.LastNativeEffects!;
        Assert.Equal(-40f, f.Document.Placements.Single(p => p.Id == "marker-1").X);
        Assert.Equal((MapNativeInvalidation.Placements, (MapBox3?)null, (MapBox3?)null),
            (effects.Invalidates, effects.OldBounds, effects.NewBounds));
    }

    [Fact]
    public void NoProviderBound_ReportsUnboundedAndInvalidatesEverything()
    {
        var f = new NativePlacementEffectsFixture(bindProvider: false);
        f.Editor.Execute(new MovePlacementCommand("crate-1", 100f, 10f, 0f));
        MapNativeEditEffects effects = f.Editor.LastNativeEffects!;
        Assert.Equal((Spatial | MapNativeInvalidation.Unbounded, (MapBox3?)null, (MapBox3?)null),
            (effects.Invalidates, effects.OldBounds, effects.NewBounds));

        MapBuiltWorld world = f.Build();
        MapNavTileCoord[] every = MapNavTiling.Partition(world, NativePlacementEffectsFixture.Grids,
            NativePlacementEffectsFixture.NavOptions).Select(t => t.Coord).ToArray();
        Assert.Equal(64, every.Length);
        MapAffectedSet affected = MapResidencyOwnership.Affected(world, NativePlacementEffectsFixture.Grids, effects);
        Assert.Equal(world.Statics.Select(s => s.OwnerId).Order(System.StringComparer.Ordinal), affected.Owners);
        // One storage tile per navigation tile on these grids.
        Assert.Equal(every, affected.StorageTiles.Select(t => new MapNavTileCoord(t.X, t.Z)));
        Assert.Equal(every, affected.NavTiles);
        Assert.Equal(every, MapNavTiling.AffectedTiles(world, NativePlacementEffectsFixture.Grids,
            NativePlacementEffectsFixture.NavOptions, effects));
    }

    [Fact]
    public void ColliderHeightEdit_WhoseRaiseChanges_ReportsTheNewEnvelope()
    {
        using var f = new NativeCollisionToolFixture();
        // The lone centred box is 1.5 m tall. Resized to 0 to 0.5 m, its envelope rises to the 1 m reach minimum.
        var edit = f.Service.SetHeights("lone-box", 0f, 0.5f, dryRun: true);
        MapBox3 old = edit.Effects.OldBounds!.Value, @new = edit.Effects.NewBounds!.Value;
        Assert.Equal(1.5, old.MaxY - old.MinY, 4);
        Assert.Equal(old.MinY + 0.75, @new.MinY, 4);
        Assert.Equal(1.0, @new.MaxY - @new.MinY, 4);
        Assert.Equal((9.5, 10.5, -8.5, -7.5), (@new.MinX, @new.MaxX, @new.MinZ, @new.MaxZ));
    }
}
