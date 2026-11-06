using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapBoundsRefusalTests
{
    [Theory]
    [InlineData("scale-nonfinite", MapDocumentRefusal.ValueNonfinite, "Scale", double.PositiveInfinity)]
    [InlineData("scale-zero", MapDocumentRefusal.ValueOutsideRange, "Scale", 0d)]
    [InlineData("position", MapDocumentRefusal.ValueNonfinite, "Position.Y", double.NaN)]
    [InlineData("yaw", MapDocumentRefusal.ValueNonfinite, "YawRadians", double.PositiveInfinity)]
    public void Placement_ReportsTheFailedComponentAndAcceptsItsRepair(string fault, MapDocumentRefusal reason,
        string field, double actual)
    {
        var fixture = NativeWorldFixtures.Crate();
        var placement = fixture.Resolved.Placements.Single();
        var shapes = MapAssetShapes.Read(fixture.Assets, placement.AssetId);
        var transform = placement.Transform;
        if (fault == "scale-nonfinite") transform = transform with { Scale = float.PositiveInfinity };
        if (fault == "scale-zero") transform = transform with { Scale = 0 };
        if (fault == "position") transform = transform with { Position = new(0, float.NaN, 0) };
        if (fault == "yaw") transform = transform with { YawRadians = float.PositiveInfinity };
        var invalid = new MapResolvedPlacement(placement.PlacementId, placement.Kind, placement.AssetId,
            placement.NumericId, transform, placement.Tags);
        var precise = new MapEffectiveTransform(0, 0, 0, 0, transform.Scale);
        var error = Assert.Throws<MapDocumentException>(() =>
            MapPlacementShapes.InvalidPlacementGeometryForTests(invalid, shapes, precise));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(placement.PlacementId, error.PlacementId);
        Assert.Equal(field, error.FieldPath);
        Assert.Equal(actual, error.ActualNumber);
        Assert.NotNull(MapPlacementShapes.Bounds(placement, shapes));
    }

    [Theory]
    [InlineData(false, MapDocumentRefusal.BoundsInvalid)]
    [InlineData(true, MapDocumentRefusal.ValueNonfinite)]
    public void ShapeBounds_DistinguishEmptyAndNonfiniteGeometry(bool nonfinite, MapDocumentRefusal reason)
    {
        PhysicsShape shape = nonfinite ? new SphereShape(float.PositiveInfinity) : new CompoundShape([]);
        var error = Assert.Throws<MapDocumentException>(() => MapShapeBounds.Of(shape, Pose.Identity));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(nonfinite ? "bounds.MinX" : "bounds", error.FieldPath);
        if (nonfinite) Assert.Equal(double.NegativeInfinity, error.ActualNumber);
        Assert.Equal(new MapBox3(-1, -1, -1, 1, 1, 1), MapShapeBounds.Of(new SphereShape(1), Pose.Identity));
    }

    [Fact]
    public void RegistrationBounds_ReportTheUnrepresentableMagnitudeAndAcceptSmallerGeometry()
    {
        // Both operands are exact normal float32 values. Their sum exceeds binary32 without cancellation.
        var shape = new BoxShape(new Vector3(float.MaxValue));
        var transform = new MapEffectiveTransform(float.MaxValue, 0, 0, 0, 1);
        var error = Assert.Throws<MapDocumentException>(() => MapRegistrationBounds.Of(shape, transform));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal("RegistrationBounds", error.Operation);
        Assert.Equal("Magnitude", error.FieldPath);
        Assert.True(error.ActualNumber > float.MaxValue);
        Assert.Equal(new MapBox3(-1, -1, -1, 1, 1, 1),
            MapRegistrationBounds.Of(new BoxShape(Vector3.One), new(0, 0, 0, 0, 1)));
    }

    [Fact]
    public void NavigationLookup_ReportsMissingSurfaceAndRecordContext()
    {
        var world = NativeWorldFixtures.BuildStackedCaveAcrossTiles();
        var reference = world.Surfaces.Witness.Records.First(r => r.Id == "seam-lower-opening");
        var surface = Assert.Throws<MapDocumentException>(() =>
            MapNavTiling.MissingSurfaceForTests(world.Surfaces, "missing"));
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, surface.Reason);
        Assert.Equal("missing", surface.SurfaceId);
        MapNavTiling.MissingSurfaceForTests(world.Surfaces, reference.Anchor.SurfaceId);

        var absent = reference with { Id = "missing" };
        var record = Assert.Throws<MapDocumentException>(() =>
            MapNavTiling.MissingRecordForTests(world.Surfaces, absent));
        Assert.Equal(MapDocumentRefusal.PatchUnavailable, record.Reason);
        Assert.Equal(absent.Id, record.RecordId);
        Assert.Equal(absent.Anchor, record.Patch);
        Assert.Equal(MapPatchStatus.Missing, record.PatchStatus);
        Assert.Equal(typeof(MapHorizontalOpening), record.ExpectedRecordType);
        MapNavTiling.MissingRecordForTests(world.Surfaces, reference);

        var room = world.Surfaces.Witness.Records.First(r => r.Id == "seam-lower-room");
        var kind = Assert.Throws<MapDocumentException>(() => MapNavTiling.MissingRecordForTests(world.Surfaces, room));
        Assert.Equal(MapDocumentRefusal.ReferenceKindMismatch, kind.Reason);
        Assert.Equal(room.Id, kind.RecordId);
        Assert.Equal(room.Anchor, kind.Patch);
        Assert.Equal(MapPatchStatus.Present, kind.PatchStatus);
        Assert.Equal(typeof(MapHorizontalOpening), kind.ExpectedRecordType);
        Assert.Equal(typeof(MapSpaceDoc), kind.RecordType);
    }
}
