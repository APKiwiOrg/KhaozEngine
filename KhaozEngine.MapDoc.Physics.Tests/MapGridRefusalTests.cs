using System;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapGridRefusalTests
{
    [Theory]
    [InlineData("size-nonfinite", MapDocumentRefusal.ValueNonfinite, "StorageTileSize")]
    [InlineData("size-zero", MapDocumentRefusal.ValueOutsideRange, "StorageTileSize")]
    [InlineData("size-fraction", MapDocumentRefusal.PhysicsGridAlignmentInvalid, "StorageTileSize")]
    [InlineData("nav-count", MapDocumentRefusal.ValueOutsideRange, "NavTileStorageTiles")]
    [InlineData("server-count", MapDocumentRefusal.ValueOutsideRange, "ServerCellStorageTiles")]
    [InlineData("origin-x", MapDocumentRefusal.ValueNonfinite, "Origin.X")]
    [InlineData("origin-z", MapDocumentRefusal.ValueNonfinite, "Origin.Y")]
    [InlineData("seam-x", MapDocumentRefusal.PhysicsGridAlignmentInvalid, "Origin.X")]
    [InlineData("seam-z", MapDocumentRefusal.PhysicsGridAlignmentInvalid, "Origin.Y")]
    public void Alignment_IdentifiesItsActualBranch(string fault, MapDocumentRefusal reason, string field)
    {
        var valid = new MapWorldGrids(64, 1, 1, Vector2.Zero);
        var broken = fault switch
        {
            "size-nonfinite" => valid with { StorageTileSize = float.NaN },
            "size-zero" => valid with { StorageTileSize = 0 },
            "size-fraction" => valid with { StorageTileSize = 0.5f },
            "nav-count" => valid with { NavTileStorageTiles = 0 },
            "server-count" => valid with { ServerCellStorageTiles = 0 },
            "origin-x" => valid with { Origin = new(float.NaN, 0) },
            "origin-z" => valid with { Origin = new(0, float.NaN) },
            "seam-x" => valid with { Origin = new(1, 0) },
            _ => valid with { Origin = new(0, 1) },
        };
        // NavTileOf checks alignment alone, so a fractional size breaks only that rule, not a world's tile identity.
        var error = Assert.Throws<MapDocumentException>(() => broken.NavTileOf(new(0, 0)));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        Assert.Equal(broken.StorageTileSize, error.StorageTileSize);
        if (fault is "nav-count" or "server-count")
        {
            Assert.Equal(0, error.ActualInteger);
            Assert.Equal(1, error.MinimumInteger);
        }
        if (fault is "origin-x" or "origin-z" or "size-nonfinite") Assert.Equal(double.NaN, error.ActualNumber);
        if (fault is "seam-x" or "seam-z") Assert.Equal(1, error.ActualNumber);
        if (field == "StorageTileSize")
        {
            Assert.Equal(1, error.MinimumNumber);
            valid = valid with { StorageTileSize = 1 };
        }
        Assert.Equal(new MapNavTileCoord(0, 0), valid.NavTileOf(new(0, 0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlockIndex_ReportsTheTileAndAcceptsTheSignedBoundary(bool server)
    {
        var invalid = new MapWorldGrids(64, 1, 1, new(64, 0));
        var tile = new MapTileCoord(int.MinValue, 0);
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            if (server) _ = invalid.ServerCellOf(tile);
            else _ = invalid.NavTileOf(tile);
        });
        Assert.Equal(MapDocumentRefusal.PhysicsGridIndexOutOfRange, error.Reason);
        Assert.Equal(server ? "server cells" : "navigation tiles", error.GridKind);
        Assert.Equal("X", error.Axis);
        Assert.Equal(int.MinValue, error.TileIndex);
        Assert.Equal(1, error.StorageTiles);
        Assert.Equal(64, error.AxisOrigin);
        Assert.Equal(int.MinValue - 1L, error.ActualInteger);
        Assert.Equal(int.MinValue, error.MinimumInteger);
        Assert.Equal(int.MaxValue, error.MaximumInteger);
        var valid = invalid with { Origin = Vector2.Zero };
        if (server) Assert.Equal(new MapServerCellCoord(int.MinValue, 0), valid.ServerCellOf(tile));
        else Assert.Equal(new MapNavTileCoord(int.MinValue, 0), valid.NavTileOf(tile));
    }
}
