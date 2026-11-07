using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.Physics;
using KhaozEngine.Movement;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapQueryRefusalTests
{
    [Theory]
    [InlineData("range", MapDocumentRefusal.ValueOutsideRange, "Range", -1d)]
    [InlineData("range-nonfinite", MapDocumentRefusal.ValueNonfinite, "Range", double.NaN)]
    [InlineData("tolerance", MapDocumentRefusal.ValueOutsideRange, "Tolerance", -1d)]
    [InlineData("tolerance-nonfinite", MapDocumentRefusal.ValueNonfinite, "Tolerance", double.NaN)]
    [InlineData("band-min", MapDocumentRefusal.ValueNonfinite, "Band.MinY", double.NaN)]
    [InlineData("band-max", MapDocumentRefusal.ValueNonfinite, "Band.MaxY", double.PositiveInfinity)]
    [InlineData("band-order", MapDocumentRefusal.BoundsInvalid, "Band", 1d)]
    public void Reach_IdentifiesTheNestedFieldAndAcceptsZeroWidthLimits(string fault, MapDocumentRefusal reason,
        string field, double actual)
    {
        var query = new MapWorldQueries(NativeWorldFixtures.BuildCrate());
        var body = new MovementBody(Vector3.Zero, 0.2f, 0.4f);
        float range = fault == "range" ? -1 : fault == "range-nonfinite" ? float.NaN : 0;
        float tolerance = fault == "tolerance" ? -1 : fault == "tolerance-nonfinite" ? float.NaN : 0;
        MapInteractionBand? band = fault switch
        {
            "band-min" => new(float.NaN, 0),
            "band-max" => new(0, float.PositiveInfinity),
            "band-order" => new(1, 0),
            _ => null,
        };
        var error = Assert.Throws<MapQueryRangeException>(() => query.Within(body, "crate", range, tolerance, band));
        Assert.IsAssignableFrom<ArgumentOutOfRangeException>(error);
        Assert.Equal(field.StartsWith("Band", StringComparison.Ordinal) ? "band" : field.ToLowerInvariant(), error.ParamName);
        Assert.Null(error.ActualValue);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        Assert.Equal(actual, error.ActualNumber);
        if (field is "Range" or "Tolerance") Assert.Equal(0, error.MinimumNumber);
        else { Assert.Equal(band!.Value.MinY, error.MinimumNumber); Assert.Equal(band.Value.MaxY, error.MaximumNumber); }
        Assert.True(query.Within(body, "crate", 0, 0, new(0, 0)));
    }

    [Fact]
    public void Stance_ReportsNonfiniteSeatsAndUnsupportedFootprints()
    {
        var world = NativeWorldFixtures.BuildCrate();
        MapStanceValidator invalid = (Vector3 candidate, out Vector3 seated) => { seated = candidate with { Z = float.NaN }; return true; };
        var error = Assert.Throws<MapStanceException>(() => MapStanceCandidates.Find(world, "crate", Vector3.Zero,
            MoveTuning.Default, new(0.25f, 0.5f), invalid));
        Assert.Equal(MapDocumentRefusal.ValueNonfinite, error.Reason);
        Assert.Equal("SeatedFeet.Z", error.FieldPath);
        Assert.Equal("crate", error.PlacementId);
        Assert.Equal(double.NaN, error.ActualNumber);
        MapStanceValidator valid = (Vector3 candidate, out Vector3 seated) => { seated = candidate; return true; };
        Assert.NotEmpty(MapStanceCandidates.Find(world, "crate", Vector3.Zero, MoveTuning.Default, new(0.25f, 0.5f), valid));
        var shape = Assert.Throws<MapStanceUnsupportedException>(() =>
            MapStanceCandidates.UnsupportedFootprintForTests(new UnknownShape()));
        Assert.IsAssignableFrom<NotSupportedException>(shape);
        Assert.Equal(MapDocumentRefusal.PhysicsShapeUnsupported, shape.Reason);
        Assert.Equal(typeof(UnknownShape), shape.ShapeType);
        MapStanceCandidates.UnsupportedFootprintForTests(new BoxShape(Vector3.One));
    }

    sealed class UnknownShape : PhysicsShape { }
}
