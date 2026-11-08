using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapNavigationRefusalTests
{
    static readonly MapWorldGrids Grids = new(64, 1, 4, Vector2.Zero);
    static readonly MapNavTileOptions Options = new(0, "profile", "controller");

    [Theory]
    [InlineData("nonfinite", MapDocumentRefusal.ValueNonfinite, "SeamMarginMetres")]
    [InlineData("negative", MapDocumentRefusal.ValueOutsideRange, "SeamMarginMetres")]
    [InlineData("profile", MapDocumentRefusal.SchemaValueRequired, "ProfileIdentity")]
    [InlineData("controller", MapDocumentRefusal.SchemaValueRequired, "ControllerIdentity")]
    public void Options_IdentifyTheNestedPolicyField(string fault, MapDocumentRefusal reason, string field)
    {
        var world = NativeWorldFixtures.BuildLegacyStrip();
        var options = fault switch
        {
            "nonfinite" => Options with { SeamMarginMetres = float.NaN },
            "negative" => Options with { SeamMarginMetres = -1 },
            "profile" => Options with { ProfileIdentity = " " },
            _ => Options with { ControllerIdentity = " " },
        };
        var error = Assert.Throws<MapNavigationArgumentException>(() => MapNavTiling.Partition(world, Grids, options));
        Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.Equal("options", error.ParamName);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        if (fault == "nonfinite") Assert.True(double.IsNaN(error.ActualNumber!.Value));
        if (fault == "negative") { Assert.Equal(-1, error.ActualNumber); Assert.Equal(0, error.MinimumNumber); }
        if (fault is "profile" or "controller") Assert.Equal(" ", error.ActualText);
        Assert.NotEmpty(MapNavTiling.Partition(world, Grids, Options));
    }

    [Theory]
    [InlineData("min-y", MapDocumentRefusal.AuthoredValueMismatch)]
    [InlineData("max-y", MapDocumentRefusal.AuthoredValueMismatch)]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("east", MapDocumentRefusal.NavigationTilesNotAdjacent)]
    [InlineData("north", MapDocumentRefusal.NavigationTilesNotAdjacent)]
    [InlineData("x-span", MapDocumentRefusal.NavigationTilesNotAdjacent)]
    [InlineData("z-span", MapDocumentRefusal.NavigationTilesNotAdjacent)]
    public void Seams_IdentifyTheTilesAndRule(string fault, MapDocumentRefusal reason)
    {
        var world = NativeWorldFixtures.BuildLegacyStrip();
        var a = new MapNavTile(new(0, 0), new(0, world.Bounds.MinY, 0, 64, world.Bounds.MaxY, 64), default, "a", "a");
        bool north = fault is "north" or "z-span";
        var b = a with { Coord = north ? new(0, 1) : new(1, 0),
            Bounds = north ? a.Bounds with { MinZ = 64, MaxZ = 128 } : a.Bounds with { MinX = 64, MaxX = 128 } };
        var broken = fault switch
        {
            "min-y" => b with { Bounds = b.Bounds with { MinY = world.Bounds.MinY - 1 } },
            "max-y" => b with { Bounds = b.Bounds with { MaxY = world.Bounds.MaxY + 1 } },
            "duplicate" => b with { Coord = a.Coord },
            "east" => b with { Bounds = b.Bounds with { MinX = 65 } },
            "north" => b with { Bounds = b.Bounds with { MinZ = 65 } },
            "x-span" => b with { Bounds = b.Bounds with { MinZ = 65, MaxZ = 129 } },
            _ => b with { Bounds = b.Bounds with { MinX = 65, MaxX = 129 } },
        };
        var error = Assert.Throws<MapNavigationArgumentException>(() => MapNavTiling.Seams([a, broken], world, Options));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(broken.Coord, error.Tile);
        Assert.Equal(fault is "min-y" or "max-y" or "duplicate" ? "tiles" : null, error.ParamName);
        if (reason == MapDocumentRefusal.NavigationTilesNotAdjacent) Assert.Equal(a.Coord, error.OtherTile);
        if (fault is "min-y" or "max-y")
        {
            Assert.Equal(fault == "min-y" ? "Bounds.MinY" : "Bounds.MaxY", error.FieldPath);
            Assert.Equal(fault == "min-y" ? world.Bounds.MinY : world.Bounds.MaxY, error.ExpectedNumber);
            Assert.Equal(fault == "min-y" ? broken.Bounds.MinY : broken.Bounds.MaxY, error.ActualNumber);
        }
        Assert.Single(MapNavTiling.Seams([a, b], world, Options));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TileRange_ReportsTheSignedBoundaryAndAcceptsItsLastIndex(bool below)
    {
        double limit = below ? int.MinValue : int.MaxValue;
        double invalid = limit + (below ? -1d : 1d);
        var error = Assert.Throws<MapDocumentException>(() => MapNavTiling.TileAxisForTests(invalid, invalid, 0, 1, "X"));
        Assert.Equal(MapDocumentRefusal.PhysicsNavIndexOutOfRange, error.Reason);
        Assert.Equal("X", error.Axis);
        Assert.Equal(invalid, error.FirstIndex);
        Assert.Equal(invalid, error.LastIndex);
        Assert.Equal(0, error.AxisOrigin);
        Assert.Equal(invalid, error.MinimumNumber);
        Assert.Equal(invalid, error.MaximumNumber);
        Assert.Equal(int.MinValue, error.MinimumInteger);
        Assert.Equal(int.MaxValue, error.MaximumInteger);
        Assert.Equal(((int)limit, (int)limit), MapNavTiling.TileAxisForTests(limit, limit, 0, 1, "X"));
    }

    [Theory]
    [InlineData(double.NaN, 1, MapDocumentRefusal.ValueNonfinite)]
    [InlineData(0, double.PositiveInfinity, MapDocumentRefusal.ValueNonfinite)]
    [InlineData(2, 1, MapDocumentRefusal.BoundsInvalid)]
    public void TileBounds_IdentifyTheFailedBranch(double min, double max, MapDocumentRefusal reason)
    {
        var error = Assert.Throws<MapNavigationArgumentException>(() => MapNavTiling.TileAxisForTests(min, max, 0, 1, "Z"));
        Assert.Equal(reason, error.Reason);
        Assert.Null(error.ParamName);
        Assert.Equal("Bounds.Z", error.FieldPath);
        Assert.Equal((1, 1), MapNavTiling.TileAxisForTests(1, 1, 0, 1, "Z"));
    }

    [Fact]
    public void OpeningPatch_ReportsTheUnavailableGeometryAndAcceptsItsRepair()
    {
        var world = NativeWorldFixtures.BuildStackedCaveAcrossTiles();
        var reference = world.Surfaces.Witness.Records.First(r => r.Id == "seam-lower-opening");
        Assert.True(world.Surfaces.TryRecord(reference, out MapTopologyRecord? record, out _));
        var opening = Assert.IsType<MapHorizontalOpening>(record);
        var missing = opening with { Patch = new("missing", 0, 0) };
        var error = Assert.Throws<MapDocumentException>(() => MapNavTiling.MissingOpeningPatchForTests(world.Surfaces, missing, Grids));
        Assert.Equal(MapDocumentRefusal.PatchUnavailable, error.Reason);
        Assert.Equal(missing.Patch, error.Patch);
        Assert.Equal(MapPatchStatus.Unloaded, error.PatchStatus);
        Assert.Equal(opening.Id, error.RecordId);
        Assert.NotEmpty(MapNavTiling.MissingOpeningPatchForTests(world.Surfaces, opening, Grids));
    }

    [Fact]
    public void PortalInterval_ReportsTheCountAndAcceptsTheMinimumSequence()
    {
        var world = NativeWorldFixtures.BuildStackedCaveAcrossTiles();
        MapLatticeVertex a = new("seam-cave-lower-floor", MapLatticeAddress.Corner(28, 0));
        MapLatticeVertex b = new("seam-cave-lower-floor", MapLatticeAddress.Corner(29, 0));
        var anchor = new MapPatchKey(a.SurfaceId, 0, 0);
        var reference = new MapRecordRef("room", anchor);
        var portal = new MapCavePortal("short-portal", reference, reference, [a], reference, null);
        var error = Assert.Throws<MapDocumentException>(() => MapNavTiling.InvalidPortalSequenceForTests(world.Surfaces, portal, Grids));
        Assert.Equal(MapDocumentRefusal.GeometryVertexSequenceInvalid, error.Reason);
        Assert.Equal(portal.Id, error.RecordId);
        Assert.Equal(1, error.ActualCount);
        Assert.Equal(2, error.MinimumInteger);
        Assert.NotEmpty(MapNavTiling.InvalidPortalSequenceForTests(world.Surfaces, portal with { Interval = [a, b] }, Grids));
    }
}
