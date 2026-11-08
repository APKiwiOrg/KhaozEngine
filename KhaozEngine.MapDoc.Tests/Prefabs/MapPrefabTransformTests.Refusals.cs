using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

public sealed partial class MapPrefabTransformTests
{
    [Theory]
    [InlineData("x", "$.x")]
    [InlineData("y", "$.y")]
    [InlineData("z", "$.z")]
    [InlineData("yaw", "$.yawRadians")]
    [InlineData("scale", "$.scale")]
    [InlineData("zero-scale", "$.scale")]
    public void EffectivePose_IdentifiesTheFirstFailedScalar(string fault, string path)
    {
        var identity = new MapEffectiveTransform(0, 0, 0, 0, 1);
        var accepted = new MapEffectiveTransform(1, 2, 3, 0, 1);
        var refused = fault switch
        {
            "x" => accepted with { X = double.NaN },
            "y" => accepted with { Y = double.PositiveInfinity },
            "z" => accepted with { Z = double.NegativeInfinity },
            "yaw" => accepted with { YawRadians = double.NaN },
            "scale" => accepted with { Scale = double.PositiveInfinity },
            _ => accepted with { Scale = 0 },
        };
        var error = Assert.Throws<MapDocumentException>(() => MapEffectiveTransform.Compose(identity, refused));
        Assert.Equal(fault == "zero-scale" ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.ValueNonfinite, error.Reason);
        Assert.Equal(path, error.FieldPath);
        if (fault == "zero-scale")
        {
            Assert.Equal(0, error.ActualNumber);
            Assert.Equal(0, error.MinimumNumber);
        }
        else Assert.Null(error.ActualNumber);
        // Finite operands are exact integers. Identity composition adds zero and multiplies by one.
        Assert.Equal(accepted, MapEffectiveTransform.Compose(identity, accepted));
    }

    [Theory]
    [InlineData(-1_000_001, 2, 3, -1_000_000, 2, 3)]
    [InlineData(1, 1_000_001, 3, 1, 1_000_000, 3)]
    [InlineData(1, 2, -1_000_001, 1, 2, -1_000_000)]
    public void WorldCoordinateLimit_RetainsTheOwnerAndBinary64Position(double x, double y, double z,
        double limitX, double limitY, double limitZ)
    {
        var error = Assert.Throws<MapDocumentException>(() => MapResourceTransform.RequireWithinWorld("terrain-chunk", x, y, z));
        Assert.Equal(MapDocumentRefusal.PrefabWorldCoordinateLimitExceeded, error.Reason);
        Assert.Equal("terrain-chunk", error.OwnerId);
        Assert.Equal(x, error.PositionX);
        Assert.Equal(y, error.PositionY);
        Assert.Equal(z, error.PositionZ);
        Assert.Equal(1_000_000L, error.Limit);
        Assert.Null(error.InstanceId);
        Assert.Null(error.PlacementId);
        Assert.Null(error.ChildKind);
        // Every coordinate and its boundary are exact normal float32 values, promoted without rounding.
        MapResourceTransform.RequireWithinWorld("terrain-chunk", limitX, limitY, limitZ);
    }
}
