using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public class MapWaterContainmentTests
{
    static MapWaterContainmentReport Check(MapDocument doc) => MapWaterContainment.Validate(CaveFixtures.View(doc), doc.Water!);

    [Fact]
    public void SharedFixtures_Validate()
    {
        MapWaterContainmentReport ocean = Check(WaterFixtures.OceanOverCaves());
        Assert.Equal((true, 0), (ocean.Complete, ocean.Errors.Count));
        MapWaterContainmentReport cave = Check(WaterFixtures.SeaConnectedCave(portalBottomY: -5f));
        Assert.Equal((true, 0), (cave.Complete, cave.Errors.Count));
    }

    // R2 lets a portal name an alias of a space as its endpoint, and connectivity follows the alias to its owner.
    [Theory]
    [InlineData("sea")]
    [InlineData("grotto")]
    [InlineData("sea,grotto")]
    public void PortalEndpointAliases_ConnectTheirOwners(string aliased)
    {
        MapWaterContainmentReport report = Check(WaterFixtures.SeaConnectedCaveThroughAliases(aliased.Split(',')));
        Assert.Equal((true, 0), (report.Complete, report.Errors.Count));
    }

    // The exact bottom 2 m - 2^-26 m is below the level while the rounded summary equals it, and a tie stays closed.
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    public void WallPortal_ConnectsByItsExactBottom_NotTheRoundedSummary(int unitsFromLevel, bool connected)
    {
        MapDocument doc = WaterFixtures.FineSeaConnectedCave(2 * WaterFixtures.FineUnitsPerMetre + unitsFromLevel, surfaceY: 2f);
        var refs = WaterFixtures.SeaCaveRefs(doc);
        MapPortalFact fact = MapRelationApertures.Compile(CaveFixtures.View(doc), refs.Portal, refs.Grotto, refs.Sea, null);
        Assert.Equal(2f, fact.Summary.BottomY);
        Assert.Equal(Math.Sign(unitsFromLevel), Math.Sign(MapRelationApertures.BottomOf(fact).CompareTo(MapExactValue.FromSingle(2f))));
        MapWaterContainmentReport report = Check(doc);
        Assert.True(report.Complete);
        Assert.Equal(!connected, report.Errors.Any(e => e.Contains("water body 'ocean': covered spaces are not connected below its surface", StringComparison.Ordinal)));
        Assert.Equal(connected ? 0 : 1, report.Errors.Count);
    }

    [Fact]
    public void InvalidPortalChain_IsRefused_NotUnverified()
    {
        MapWaterContainmentReport report = Check(WaterFixtures.SeaConnectedCaveWithInvalidPortalChain());
        Assert.True(report.Complete);
        Assert.Contains(report.Errors, e => e.Contains("water body 'ocean': portal 'grotto-mouth' is invalid (invalid: chain 'grotto-mouth-bottom'", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Errors, e => e.Contains("unverified", StringComparison.Ordinal));
    }

    [Fact]
    public void SeaConnectedCave_IsOneBodyAcrossThePortal()
    {
        MapDocument doc = WaterFixtures.SeaConnectedCave(portalBottomY: -5f);
        MapWaterSample s = new MapWaterQuery(CaveFixtures.View(doc), doc.Water!).Sample(WaterFixtures.GrottoPoint(y: -1f));
        Assert.Equal((MapWaterStatus.Wet, "ocean", "grotto"), (s.Status, s.BodyId, s.SpaceId));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(null)]
    public void SeaConnectedCave_RefusesWhenTheConnectionIsDryOrAbsent(float? portalBottomY) =>
        Assert.Contains(Check(WaterFixtures.SeaConnectedCave(portalBottomY)).Errors,
            e => e.Contains("water body 'ocean': covered spaces are not connected below its surface", StringComparison.Ordinal));

    [Theory]
    [InlineData(-10f, true)]
    [InlineData(0f, false)]
    [InlineData(1f, false)]
    public void Sinkhole_ConnectsOnlyBelowTheSurface(float openingY, bool connected)
    {
        IReadOnlyList<string> errors = Check(WaterFixtures.SinkholeCave(openingY)).Errors;
        Assert.Equal(!connected, errors.Any(e => e.Contains("water body 'ocean': covered spaces are not connected below its surface", StringComparison.Ordinal)));
    }

    [Fact]
    public void Sinkhole_WithUnavailableOpening_IsUnverified()
    {
        MapDocument doc = WaterFixtures.SinkholeCave(-10f);
        MapScopedSurfaces view = WaterFixtures.ViewWithUnloaded(doc, "sinkhole-floor");
        var refs = WaterFixtures.SinkholeRefs(doc);
        Assert.True(WaterFixtures.RecordsAcquired(view, refs.Sea, refs.Sink, refs.SeaFootprint, refs.SinkFootprint, refs.Link));
        Assert.Contains(view.Witness.Unavailable, u => u.Key == refs.OpeningOwner && u.Status == MapPatchStatus.Unloaded);
        Assert.Throws<MapDocumentException>(() => MapRelationApertures.Compile(view, refs.Opening, refs.Sea, refs.Sink, refs.Link));
        MapWaterContainmentReport report = MapWaterContainment.Validate(view, doc.Water!);
        Assert.False(report.Complete);
        Assert.Contains(report.Errors, e => e.Contains("water body 'ocean': unverified", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("domain-off-footprint", "water body 'ocean' covers no space footprint")]
    [InlineData("missing-cover", "water body 'ocean': cover 'nowhere' is not a space")]
    [InlineData("alias-cover", "water body 'ocean': cover 'sea-alias' is an alias")]
    public void BadCovers_Refuse(string variant, string message) =>
        Assert.Contains(Check(WaterFixtures.Broken(variant)).Errors, e => e.Contains(message, StringComparison.Ordinal));

    // A complete acquisition holds every patch of every declared surface, so an anchor on an undeclared surface is absent.
    [Fact]
    public void CoverOnAnAbsentPatch_IsNotASpace()
    {
        MapWaterContainmentReport report = Check(WaterFixtures.Broken("absent-cover-patch"));
        Assert.True(report.Complete);
        Assert.Contains(report.Errors, e => e.Contains("water body 'ocean': cover 'nowhere' is not a space", StringComparison.Ordinal));
    }

    [Fact]
    public void Footprints_ReadOnlyTheSlotsTheWindowMeets()
    {
        MapDocument doc = WaterFixtures.PondWithFarFootprints();
        MapScopedSurfaces view = CaveFixtures.View(doc);
        var far = new MapPatchKey("far-floor", 10, 0);
        Assert.True(view.Witness.Complete);
        Assert.True(WaterFixtures.RecordsAcquired(view, new MapRecordRef("ghost-cells", far), new MapRecordRef("shallows-far-cells-10-0", far)));
        Assert.False(view.TryRecord(WaterFixtures.GhostSpace, out _, out _));

        MapWaterDomain domain = MapWaterDomain.Compile(doc.Water!.Bodies.Single().Domain);
        IReadOnlyList<MapWaterFootprintRect> rects = MapWaterFootprints.Of(view, "shallows", domain.Min, domain.Max,
            out bool complete, out int cellsInspected, out IReadOnlyList<MapPatchKey> patchesRead);
        Assert.True(complete);
        Assert.Equal(16, cellsInspected);
        Assert.Equal(new[] { new MapPatchKey("pond-floor", 0, 0) }, patchesRead);
        Assert.Equal(new[] { (0L, 0L, 4L, 4L) }, rects.Select(r => (r.CellMinX, r.CellMinZ, r.CellMaxX, r.CellMaxZ)));

        MapWaterContainmentReport report = MapWaterContainment.Validate(view, doc.Water!);
        Assert.Equal((true, 0), (report.Complete, report.Errors.Count));
    }

    // The greedy row-run merge of TileWaterBodies.Rectangles, stated by its expected rectangles: rows in ascending Z,
    // each maximal X run extends the rectangle of the row before when its span is identical.
    [Fact]
    public void Footprints_MergeAcrossSlotsInANegativeZFrame()
    {
        MapScopedSurfaces view = CaveFixtures.View(WaterFixtures.SlotStraddlingSpace());
        var windowMin = new MapExactXz(new MapExactValue(60, 1), new MapExactValue(-68, 1));
        var windowMax = new MapExactXz(new MapExactValue(68, 1), new MapExactValue(-62, 1));
        IReadOnlyList<MapWaterFootprintRect> rects = MapWaterFootprints.Of(view, "straddle", windowMin, windowMax,
            out bool complete, out int cellsInspected, out IReadOnlyList<MapPatchKey> patchesRead);
        Assert.True(complete);
        Assert.Equal(44, cellsInspected);
        Assert.Equal(new[] { (0L, 0L), (1L, 0L), (0L, 1L), (1L, 1L) }.Select(s => new MapPatchKey("straddle-floor", s.Item1, s.Item2)).Order(),
            patchesRead);
        Assert.Equal(new[] { (60L, 62L, 68L, 66L), (60L, 66L, 64L, 68L), (66L, 66L, 68L, 68L) },
            rects.Select(r => (r.CellMinX, r.CellMinZ, r.CellMaxX, r.CellMaxZ)));
        Assert.Equal((new MapExactXz(new(60, 1), new(-66, 1)), new MapExactXz(new(68, 1), new(-62, 1))), (rects[0].Min, rects[0].Max));
        Assert.Equal((new MapExactXz(new(66, 1), new(-68, 1)), new MapExactXz(new(68, 1), new(-66, 1))), (rects[2].Min, rects[2].Max));
    }

    [Fact]
    public void IncompleteView_IsUnverified_NotValid()
    {
        MapWaterContainmentReport report = MapWaterContainment.Validate(WaterFixtures.ViewWithUnloaded(WaterFixtures.OceanOverCaves(), "lake-floor"),
            WaterFixtures.OceanOverCaves().Water!);
        Assert.False(report.Complete);
        Assert.Contains(report.Errors, e => e.Contains("water body 'cave-lake': unverified", StringComparison.Ordinal));
    }

    [Fact]
    public void MaterialClosure_IsCheckedOnBothIdentityPaths()
    {
        MapDocument doc = WaterFixtures.OceanOverCaves();
        Assert.Equal(MapAuthoredIdentityV2.Compute(doc, WaterFixtures.Assets(), WaterFixtures.ResolveOptions()),
            MapAuthoredIdentityV2.Compute(WaterFixtures.StoredSource(doc), WaterFixtures.Assets(), WaterFixtures.ResolveOptions()));
        MapDocument bad = WaterFixtures.Broken("unknown-material");
        const string message = "water body 'ocean': render material 'no-such-material' is not a Material resource";
        Assert.Contains(message, Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(bad, WaterFixtures.Assets(), WaterFixtures.ResolveOptions())).Message, StringComparison.Ordinal);
        Assert.Contains(message, Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(WaterFixtures.StoredSource(bad), WaterFixtures.Assets(), WaterFixtures.ResolveOptions())).Message, StringComparison.Ordinal);

        // Water enters the resolver-2 authored identity on both paths, so a level edit moves both tokens alike.
        MapDocument raised = WaterFixtures.OceanOverCaves();
        int ocean = raised.Water!.Bodies.FindIndex(b => b.Id == "ocean");
        raised.Water.Bodies[ocean] = raised.Water.Bodies[ocean] with { SurfaceY = 0.5f };
        string before = MapAuthoredIdentityV2.Compute(doc, WaterFixtures.Assets(), WaterFixtures.ResolveOptions());
        string after = MapAuthoredIdentityV2.Compute(raised, WaterFixtures.Assets(), WaterFixtures.ResolveOptions());
        Assert.NotEqual(before, after);
        Assert.Equal(after, MapAuthoredIdentityV2.Compute(WaterFixtures.StoredSource(raised), WaterFixtures.Assets(), WaterFixtures.ResolveOptions()));
        Assert.NotEqual(MapAuthoredIdentityV2.Compute(WaterFixtures.StoredSource(doc), WaterFixtures.Assets(), WaterFixtures.ResolveOptions()),
            MapAuthoredIdentityV2.Compute(WaterFixtures.StoredSource(raised), WaterFixtures.Assets(), WaterFixtures.ResolveOptions()));
    }
}
