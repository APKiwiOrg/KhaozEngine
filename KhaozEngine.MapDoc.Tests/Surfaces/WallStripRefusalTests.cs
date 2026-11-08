using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class WallStripRefusalTests
{
    static readonly MapWallStrip Strip = new("strip", new("lower", new("s", 0, 0)),
        new("upper", new("s", 0, 0)), MapStripFacing.Front, 1);

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4098, 4097)]
    public void Chain_cardinality_keeps_its_inclusive_limits(int refused, int accepted)
    {
        var error = Assert.Throws<MapDocumentException>(() => MapWallStripCompiler.Compile(Strip, Chain(refused, 0), Chain(refused, 1)));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, error.Reason);
        Assert.Equal("strip", error.RecordId);
        Assert.Equal("Lower.Points", error.FieldPath);
        Assert.Equal((long)refused, error.ActualCount);
        Assert.Equal(2L, error.MinimumInteger);
        Assert.Equal(4097L, error.MaximumInteger);
        Assert.NotEmpty(MapWallStripCompiler.Compile(Strip, Chain(accepted, 0), Chain(accepted, 1)).Faces);
    }

    [Fact]
    public void Upper_chain_must_match_the_lower_count()
    {
        var error = Assert.Throws<MapDocumentException>(() => MapWallStripCompiler.Compile(Strip, Chain(2, 0), Chain(3, 1)));
        Assert.Equal(MapDocumentRefusal.SchemaArrayLengthMismatch, error.Reason);
        Assert.Equal("strip", error.RecordId);
        Assert.Equal("Upper.Points", error.FieldPath);
        Assert.Equal(3L, error.ActualCount);
        Assert.Equal(2L, error.ExpectedCount);
        Assert.NotEmpty(MapWallStripCompiler.Compile(Strip, Chain(2, 0), Chain(2, 1)).Faces);
    }

    [Fact]
    public void Facing_identifies_the_undefined_enum_value()
    {
        var error = Assert.Throws<MapDocumentException>(() => MapWallStripCompiler.Compile(
            Strip with { Facing = (MapStripFacing)255 }, Chain(2, 0), Chain(2, 1)));
        Assert.Equal(MapDocumentRefusal.SchemaEnumUnsupported, error.Reason);
        Assert.Equal("strip", error.RecordId);
        Assert.Equal("Facing", error.FieldPath);
        Assert.Equal(typeof(MapStripFacing), error.EnumType);
        Assert.Equal(255L, error.ActualInteger);
        Assert.NotEmpty(MapWallStripCompiler.Compile(Strip, Chain(2, 0), Chain(2, 1)).Faces);
    }

    [Fact]
    public void Exact_vertical_extent_overflow_keeps_the_strip_identity()
    {
        // Both chains fit int64, but their vertical extent is 2^64-1 and cannot be narrowed.
        var error = Assert.Throws<MapDocumentException>(() => MapWallStripCompiler.Compile(
            Strip, Chain(2, long.MinValue), Chain(2, long.MaxValue)));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal("strip", error.RecordId);
        Assert.Equal("wall strip geometry", error.Operation);
        Assert.NotEmpty(MapWallStripCompiler.Compile(Strip, Chain(2, 0), Chain(2, 1)).Faces);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Computed_normal_fault_names_its_segment(bool x)
    {
        var error = Assert.Throws<MapDocumentException>(() => MapWallStripCompiler.RequireFiniteNormal(
            x ? new(float.NaN, 0, 1) : new(1, 0, float.PositiveInfinity), "strip", 3));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal("strip", error.RecordId);
        Assert.Equal(3, error.Index);
        Assert.Equal("wall strip normal", error.Operation);
        MapWallStripCompiler.RequireFiniteNormal(Vector3.UnitX, "strip", 3);
    }

    internal static MapChainResolution Chain(int count, long height) => new(MapResolveStatus.Resolved,
        Enumerable.Range(0, count).Select(i => new MapExactPoint(new(i, 1), new(height, 1), default)).ToArray(), null);
}
