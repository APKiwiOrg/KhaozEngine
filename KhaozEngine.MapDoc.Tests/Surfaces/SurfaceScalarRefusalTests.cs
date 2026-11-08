using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceScalarRefusalTests
{
    [Theory]
    [InlineData(0, 1, "numerator")]
    [InlineData(1, 0, "denominator")]
    public void Unit_parts_identify_the_first_nonpositive_part(int numerator, int denominator, string field)
    {
        var ex = Assert.Throws<MapDocumentException>(() => new MapRational(numerator, denominator));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, ex.Reason);
        Assert.Equal(field, ex.FieldPath);
        Assert.Equal(0L, ex.ActualInteger);
        Assert.Equal(1L, ex.MinimumInteger);
        Assert.Equal(new MapExactValue(1, 1), new MapRational(1, 1).Exact());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(65, 64)]
    public void Address_creation_retains_the_denominator_and_its_boundary(int refused, int accepted)
    {
        var ex = Assert.Throws<MapDocumentException>(() => MapLatticeAddress.Create(1, 0, refused));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, ex.Reason);
        Assert.Equal("denominator", ex.FieldPath);
        Assert.Equal((long)refused, ex.ActualInteger);
        Assert.Equal(1L, ex.MinimumInteger);
        Assert.Equal(refused == 0 ? null : (long?)64, ex.MaximumInteger);
        Assert.Equal(accepted, MapLatticeAddress.Create(1, 0, accepted).Denominator);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Frame_enumerations_identify_the_failed_branch(bool row)
    {
        var frame = MapLatticeFrame.ImportedMetreCentimetre;
        var bad = row ? frame with { RowDirection = (MapRowDirection)2 } : frame with { Datum = (MapHeightDatum)1 };
        var ex = Assert.Throws<MapDocumentException>(() => bad.WorldXz(MapLatticeAddress.Corner(0, 0)));
        Assert.Equal(MapDocumentRefusal.SchemaEnumUnsupported, ex.Reason);
        Assert.Equal(row ? nameof(MapLatticeFrame.RowDirection) : nameof(MapLatticeFrame.Datum), ex.FieldPath);
        Assert.Equal(row ? typeof(MapRowDirection) : typeof(MapHeightDatum), ex.EnumType);
        Assert.Equal(row ? 2L : 1L, ex.ActualInteger);
        Assert.Equal(default, frame.WorldXz(MapLatticeAddress.Corner(0, 0)));
    }

    [Theory]
    [InlineData(null, 0, MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("r", -1, MapDocumentRefusal.ValueOutsideRange)]
    [InlineData("r", 4096, MapDocumentRefusal.ValueOutsideRange)]
    public void Lattice_cells_distinguish_missing_surface_from_slot_range(string? surface, int slot, MapDocumentRefusal reason)
    {
        var key = new MapPatchKey(surface!, 0, 0);
        var frame = MapLatticeFrame.ImportedMetreCentimetre;
        var ex = Assert.Throws<MapDocumentException>(() => MapLatticeRanges.CellRect(frame, key, slot));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal(key, ex.Patch);
        Assert.Equal(surface is null ? "SurfaceId" : "slotCell", ex.FieldPath);
        if (surface is not null)
        {
            Assert.Equal((long)slot, ex.ActualInteger);
            Assert.Equal(0L, ex.MinimumInteger);
            Assert.Equal(4095L, ex.MaximumInteger);
        }
        Assert.Equal(2, MapLatticeRanges.CellRect(frame, new("r", 0, 0), slot < 0 ? 0 : 4095).Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reversed_rectangles_identify_the_unordered_axis(bool x)
    {
        var min = new MapExactXz(new(x ? 2 : 0, 1), new(x ? 0 : 2, 1));
        var max = new MapExactXz(new(1, 1), new(1, 1));
        var frame = MapLatticeFrame.ImportedMetreCentimetre;
        var ex = Assert.Throws<MapDocumentException>(() => MapLatticeRanges.CellRange(frame, min, max));
        Assert.Equal(MapDocumentRefusal.BoundsInvalid, ex.Reason);
        Assert.Equal(x ? "X" : "Z", ex.FieldPath);
        Assert.Equal(2d, ex.MinimumNumber);
        Assert.Equal(1d, ex.MaximumNumber);
        Assert.Equal(new MapCellRect(1, -1, 1, -1), MapLatticeRanges.CellRange(frame, max, max));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Patch_cell_access_distinguishes_rectangle_from_coordinate(bool rectangle)
    {
        var patch = SurfacePatchFixtures.Row(2);
        if (rectangle) patch.CellMinX = 63;
        var ex = Assert.Throws<MapDocumentException>(() => patch.IsPresent(rectangle ? 0 : 2, 0));
        Assert.Equal(rectangle ? MapDocumentRefusal.BoundsInvalid : MapDocumentRefusal.SurfaceAddressOutOfPatch, ex.Reason);
        Assert.Equal(patch.Key, ex.Patch);
        Assert.Equal(rectangle ? 0 : 2, ex.CellX);
        Assert.Equal(0, ex.CellZ);
        Assert.Equal(2, ex.Width);
        Assert.Equal(1, ex.Depth);
        if (!rectangle)
        {
            Assert.Equal("x", ex.FieldPath);
            Assert.Equal(2L, ex.ActualInteger);
            Assert.Equal(0L, ex.MinimumInteger);
            Assert.Equal(1L, ex.MaximumInteger);
        }
        patch.CellMinX = 62;
        Assert.True(patch.IsPresent(1, 0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Presence_access_distinguishes_null_from_wrong_length(bool missing)
    {
        var patch = SurfacePatchFixtures.Row(1);
        patch.Presence = missing ? null! : Array.Empty<ulong>();
        var ex = Assert.Throws<MapDocumentException>(() => patch.IsPresent(0, 0));
        Assert.Equal(missing ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.SchemaArrayLengthMismatch, ex.Reason);
        Assert.Equal(nameof(MapSurfacePatch.Presence), ex.FieldPath);
        Assert.Equal(patch.Key, ex.Patch);
        Assert.Equal(missing ? null : (long?)0, ex.ActualCount);
        Assert.Equal(1L, ex.ExpectedCount);
        patch.Presence = [1];
        Assert.True(patch.IsPresent(0, 0));
    }

    [Fact]
    public void Patch_keys_identify_the_required_surface()
    {
        var ex = Assert.Throws<MapDocumentException>(() => MapPatchKey.ForCell("", 0, 0));
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, ex.Reason);
        Assert.Equal("surfaceId", ex.FieldPath);
        Assert.Equal(new MapPatchKey("r", 0, 0), MapPatchKey.ForCell("r", 0, 0));
    }
}
