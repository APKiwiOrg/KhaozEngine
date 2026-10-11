using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Water;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeWaterCommandTests
{
    [Fact]
    public void Snapshot_PreservesWater()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        MapDocument clone = NativeDocumentSnapshot.Clone(doc, MapDocRegistry.CreateDefault());
        Assert.Equal(doc.Water!.Digest(), clone.Water!.Digest());
        Assert.NotSame(doc.Water, clone.Water);
        Assert.NotSame(doc.Water.Bodies, clone.Water.Bodies);
    }

    [Fact]
    public void WaterCommand_UndoRedoRestoresTheExactSet()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string before = doc.Water!.Digest();
        var command = new WaterEditCommand(new MapUpsertWaterBody(doc.Water.Bodies.Single(b => b.Id == "cave-lake") with { SurfaceY = -33f }));
        command.Apply(doc);
        Assert.Equal(-33f, doc.Water!.Bodies.Single(b => b.Id == "cave-lake").SurfaceY);
        command.Revert(doc);
        Assert.Equal(before, doc.Water!.Digest());
        command.Apply(doc);
        Assert.Equal(-33f, doc.Water!.Bodies.Single(b => b.Id == "cave-lake").SurfaceY);
    }

    [Fact]
    public void RejectedEdit_LeavesTheDocumentUntouched()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string before = MapDocumentFile.SaveText(doc);
        var overlapping = new WaterEditCommand(new MapUpsertWaterBody(WaterFixtures.SecondLakeOverlappingCaveLake()));
        Assert.Throws<MapDocumentException>(() => overlapping.Apply(doc));
        Assert.Equal(before, MapDocumentFile.SaveText(doc));
    }

    [Fact]
    public void TerrainEditUnderABody_NamesTheBody_AndKeepsItsLevel()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string water = doc.Water!.Digest();
        MapNativeEditEffects effects = NativeDocumentTransaction.Run(doc, new TerrainEditCommand(WaterFixtures.LowerSeabedAt(2, -2, metres: 1f)), false, null, localOnly: true);
        Assert.Contains("ocean", effects.WaterBodyIds);
        Assert.True(effects.Invalidates.HasFlag(MapNativeInvalidation.Medium));
        Assert.Equal(water, doc.Water!.Digest());
    }

    [Fact]
    public void TerrainEditThroughAnAlias_NamesTheBody_OnApplyUndoAndRedo()
    {
        MapDocument doc = WaterFixtures.SeaConnectedCaveThroughAliases("grotto");
        string before = Assert.Single(MapWaterCompiler.Compile(CaveFixtures.View(doc), doc.Water!)).Digest;
        var command = new TerrainEditCommand(new MapSetCornerHeights(new("grotto-roof", 0, 0), 2, 2, 1, 1, new[] { 490 }));
        MapNativeEditEffects apply = NativeDocumentTransaction.Run(doc, command, false, null, localOnly: true);
        string after = Assert.Single(MapWaterCompiler.Compile(CaveFixtures.View(doc), doc.Water!)).Digest;
        Assert.NotEqual(before, after);
        Assert.Equal(new[] { "grotto-alias" }, apply.SpaceIds);
        AssertNamed(apply);
        MapNativeEditEffects undo = NativeDocumentTransaction.Run(doc, command, true, null, localOnly: true);
        Assert.Equal(before, Assert.Single(MapWaterCompiler.Compile(CaveFixtures.View(doc), doc.Water!)).Digest);
        AssertNamed(undo);
        MapNativeEditEffects redo = NativeDocumentTransaction.Run(doc, command, false, null, localOnly: true);
        Assert.Equal(after, Assert.Single(MapWaterCompiler.Compile(CaveFixtures.View(doc), doc.Water!)).Digest);
        AssertNamed(redo);

        static void AssertNamed(MapNativeEditEffects effects)
        {
            Assert.Equal(new[] { "ocean" }, effects.WaterBodyIds);
            Assert.True(effects.Invalidates.HasFlag(MapNativeInvalidation.Medium));
        }
    }

    [Fact]
    public void UnrelatedTransactions_KeepTheExactWaterSet()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string water = doc.Water!.Digest();
        NativeDocumentTransaction.Run(doc, new TerrainEditCommand(WaterFixtures.LowerSeabedAt(11, -2, metres: 0.5f)), false, null, localOnly: true);
        Assert.Equal(water, doc.Water!.Digest());
        NativeDocumentTransaction.Run(doc, WaterFixtures.AddMarkerPlacementCommand(), false, null, localOnly: true);
        Assert.Equal(water, doc.Water!.Digest());
    }

    [Fact]
    public void UncontainedBody_IsRefusedAtPublication()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string before = MapDocumentFile.SaveText(doc);
        MapWaterBodyDoc stray = doc.Water!.Bodies.Single(b => b.Id == "ocean") with
        {
            Domain = new MapWaterDomainDoc(MapLatticeFrame.ImportedMetreCentimetre, new[] { new MapWaterRect(30, 0, 34, 4) }, Array.Empty<MapWaterRing>()),
        };
        var error = Assert.Throws<MapDocumentException>(() => new WaterEditCommand(new MapUpsertWaterBody(stray)).Apply(doc));
        Assert.Contains("water body 'ocean' covers no space footprint", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, MapDocumentFile.SaveText(doc));
    }

    [Theory]
    [InlineData("placement")]
    [InlineData("terrain")]
    public void UnrelatedEdit_OnAnUncontainedDocument_IsRefused(string edit)
    {
        MapDocument doc = WaterFixtures.Broken("domain-off-footprint");
        string before = MapDocumentFile.SaveText(doc);
        IEditorCommand command = edit == "placement" ? WaterFixtures.AddMarkerPlacementCommand()
            : new TerrainEditCommand(WaterFixtures.LowerSeabedAt(11, -2, metres: 0.5f));
        var error = Assert.Throws<MapDocumentException>(() => NativeDocumentTransaction.Run(doc, command, false, null, localOnly: true));
        Assert.Contains("water body 'ocean' covers no space footprint", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, MapDocumentFile.SaveText(doc));
    }

    [Fact]
    public void BoundedDocument_HidesTheLegacyPlane()
    {
        MapDocument bounded = WaterFixtures.OceanOverCaves();
        Assert.Null(ViewportWorld.LegacyWaterLevel(bounded));
        bounded.Water = null;
        bounded.Terrain.WaterLevel = 1.5f;
        Assert.Equal(1.5f, ViewportWorld.LegacyWaterLevel(bounded));
    }

    [Fact]
    public void SaveReload_AfterWaterEdits_IsByteStable()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        new WaterEditCommand(new MapRemoveWaterBody("flooded")).Apply(doc);
        string text = MapDocumentFile.SaveText(doc);
        Assert.Equal(text, MapDocumentFile.SaveText(MapDocumentFile.LoadText(text)));
    }
}

public sealed class WaterPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Publication_DeepCopiesTheCandidateWater(bool writeSetOnly)
    {
        MapDocument target = WaterFixtures.OceanOverCaves();
        MapDocument candidate = NativeDocumentSnapshot.Clone(target, MapDocRegistry.CreateDefault());
        if (writeSetOnly)
            NativeDocumentSnapshot.PublishWriteSet(target, candidate, MapNativeWriteSet.PlacementsOnly with { Placements = false, Water = true });
        else NativeDocumentSnapshot.Publish(target, candidate);
        string published = target.Water!.Digest();
        MapWaterBodyDoc body = candidate.Water!.Bodies[0];
        ((MapWaterRect[])body.Domain.Rects)[0] = new(40, 0, 46, 4);
        ((MapRecordRef[])body.Covers)[0] = new("nowhere", new("seabed", 0, 0));
        candidate.Water.Media.Clear();
        candidate.Water.Bodies.Clear();
        Assert.Equal(published, target.Water.Digest());
    }

    [Theory]
    [InlineData("placement")]
    [InlineData("water")]
    public void UnverifiedContainment_RefusesEveryPublicationAtomically(string edit)
    {
        MapDocument doc = WaterFixtures.SeaConnectedCave(-5f);
        doc.Surfaces.Patches[new("grotto-floor", 0, 0)].Records.RemoveAll(r => r.Id == "grotto-mouth-top");
        MapWaterContainmentReport report = MapWaterContainment.Validate(MapScopedSurfaces.CompleteView(doc.Surfaces), doc.Water!);
        Assert.False(report.Complete);
        string before = MapSurfaceSemantics.RootDigest(doc);
        IEditorCommand command = edit == "placement" ? WaterFixtures.AddMarkerPlacementCommand()
            : new WaterEditCommand(new MapUpsertWaterMedium(new("sea-water", MapWaterMediumKind.Water, 0.5f)));
        var error = Assert.Throws<MapDocumentException>(() => NativeDocumentTransaction.Run(doc, command, false, null, localOnly: true));
        Assert.Contains("unverified", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, MapSurfaceSemantics.RootDigest(doc));
        Assert.Empty(doc.Placements);
        Assert.Equal(0, doc.NumericIdHighWaterMark);
    }

    [Fact]
    public void BoundedSwitchAndMediumEdit_UndoRedoRestoreTheExactSets()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        var medium = new WaterEditCommand(new MapUpsertWaterMedium(new("sea-water", MapWaterMediumKind.Water, 0.5f)));
        string before = doc.Water!.Digest();
        medium.Apply(doc);
        string after = doc.Water!.Digest();
        medium.Revert(doc);
        Assert.Equal(before, doc.Water!.Digest());
        medium.Apply(doc);
        Assert.Equal(after, doc.Water!.Digest());
        doc.Water.Bodies.Clear();
        var legacy = new WaterEditCommand(new MapSetBoundedWater(false));
        string bounded = doc.Water.Digest();
        legacy.Apply(doc);
        Assert.Null(doc.Water);
        legacy.Revert(doc);
        Assert.Equal(bounded, doc.Water!.Digest());
        legacy.Apply(doc);
        Assert.Null(doc.Water);
    }

    [Fact]
    public void UndoEffects_SwapBoundsAndDigestChanges()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        var command = new WaterEditCommand(new MapUpsertWaterBody(doc.Water!.Bodies.Single(b => b.Id == "cave-lake") with { SurfaceY = -33f }));
        MapNativeEditEffects forward = NativeDocumentTransaction.Run(doc, command, false, null, localOnly: true);
        MapNativeEditEffects backward = NativeDocumentTransaction.Run(doc, command, true, null, localOnly: true);
        Assert.Equal(forward.NewBounds, backward.OldBounds);
        Assert.Equal(forward.OldBounds, backward.NewBounds);
        MapDigestChange change = Assert.Single(forward.DigestChanges);
        Assert.Equal(new MapDigestChange(change.Key, change.After, change.Before), Assert.Single(backward.DigestChanges));
    }

    [Fact]
    public void BoundPublication_ValidatesContainmentAndPreservesWater()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        string water = doc.Water!.Digest();
        NativeDocumentTransaction.Run(doc, new TerrainEditCommand(WaterFixtures.LowerSeabedAt(11, -2, 0.5f)),
            false, WaterFixtures.Assets());
        Assert.Equal(water, doc.Water!.Digest());
        MapDocument broken = WaterFixtures.Broken("domain-off-footprint");
        string before = MapDocumentFile.SaveText(broken);
        Assert.Contains("covers no space footprint", Assert.Throws<MapDocumentException>(() => NativeDocumentTransaction.Run(broken,
            new TerrainEditCommand(WaterFixtures.LowerSeabedAt(11, -2, 0.5f)), false, WaterFixtures.Assets())).Message,
            StringComparison.Ordinal);
        Assert.Equal(before, MapDocumentFile.SaveText(broken));
    }
}
