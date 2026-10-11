using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
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
        Assert.Empty(Check(WaterFixtures.SeaConnectedCave(portalBottomY: -5f)).Errors);
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
