using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed class MapResourceTransformRefusalTests
{
    [Theory]
    [InlineData(double.NaN, MapDocumentRefusal.ValueNonfinite, 1_000_000)]
    [InlineData(double.PositiveInfinity, MapDocumentRefusal.ValueNonfinite, 1_000_000)]
    [InlineData(1_000_001, MapDocumentRefusal.ValueOutsideRange, 1_000_000)]
    [InlineData(-1_000_001, MapDocumentRefusal.ValueOutsideRange, -1_000_000)]
    public void CoordinateFreeze_ReportsTheFailedDomainAndAcceptsItsInclusiveBoundary(
        double value, MapDocumentRefusal reason, double control)
    {
        var error = Assert.Throws<MapDocumentException>(() => MapResourceTransform.Freeze(value));
        Assert.Equal(reason, error.Reason);
        Assert.Equal("Freeze", error.Operation);
        Assert.Equal(value, error.ActualNumber);
        if (reason == MapDocumentRefusal.ValueOutsideRange)
        {
            Assert.Equal(-1_000_000d, error.MinimumNumber);
            Assert.Equal(1_000_000d, error.MaximumNumber);
        }
        Assert.Equal(control, MapResourceTransform.Freeze(control).ToDouble());
    }

    [Fact]
    public void FrameConversion_ReportsTheLocalRadiusAndAcceptsTheBoundary()
    {
        var kernel = new MapResourceTransform(new(0, 0, 0, 0, 1));
        long radius = (long)WorldFrame.MaxLocalRadius;
        var error = Assert.Throws<MapDocumentException>(() => kernel.ToFrame(new(new(radius + 1, 1), default, default), WorldFrame.Origin));
        Assert.Equal(MapDocumentRefusal.FrameRadiusExceeded, error.Reason);
        Assert.Equal("ToFrame", error.Operation);
        Assert.Equal(radius, error.Limit);
        Assert.Equal(radius + 1d, error.PositionX);
        Assert.Equal(0d, error.PositionZ);
        Assert.Equal((float)radius, kernel.ToFrame(new(new(radius, 1), default, default), WorldFrame.Origin).X);
    }
}
