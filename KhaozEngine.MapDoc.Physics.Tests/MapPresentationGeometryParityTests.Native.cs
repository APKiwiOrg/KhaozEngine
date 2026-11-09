using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed partial class MapPresentationGeometryParityTests
{
    [Theory]
    [InlineData("crate")]
    [InlineData("short-post")]
    [InlineData("hanging-post")]
    [InlineData("fallen-log")]
    [InlineData("mesh-ramp")]
    [InlineData("doorway")]
    public void PreparedPresentation_MatchesReleasedColliderAndSelectionEnvelope(string id)
    {
        var fixture = NativeWorldFixtures.Placed(id, yaw: .371f);
        using var native = new NativeSceneFixture(fixture.Document, fixture.Assets);
        var scene = NativeSceneFixtures.Open(native, false).Acquire(Scope()).Scene!;
        var prepared = MapPresentationGeometry.Prepare(scene, fixture.Assets);
        var actual = Assert.Single(prepared.Placements);
        var released = Assert.Single(MapPlacementShapes.Resolve(fixture.Resolved));
        Assert.Equal(released.PlacementId, actual.OwnerId); Assert.Equal(released.NumericId, actual.NumericId);
        Assert.Equal(PresentationGeometryFixtures.Shape(released.Collider!), PresentationGeometryFixtures.Shape(actual.Collider!));
        Assert.Equal(PresentationGeometryFixtures.Shape(released.Envelope.Shape), PresentationGeometryFixtures.Shape(actual.Selection!));
        Assert.Equal(released.WorldPose, actual.Pose); Assert.Equal(released.Envelope.WorldPose, actual.SelectionPose);
        Assert.Equal(released.Envelope.RaiseMetres, actual.RaiseMetres);
        Assert.Equal(MapShapeBounds.Of(released.Collider!, released.WorldPose), actual.ColliderBounds);
        Assert.Equal(released.Envelope.Bounds, actual.SelectionBounds);
        var ray = new KhaozEngine.Physics.ShapeRay(.1, .1, -4, 0, 0, 1, 10);
        foreach (bool selection in new[] { false, true })
        {
            var source = selection ? released.Envelope.Shape : released.Collider!;
            var pose = selection ? released.Envelope.WorldPose : released.WorldPose;
            bool expected = KhaozEngine.Physics.ShapeRayQueries.Cast(ray, source, pose, out var expectedHit);
            Assert.Equal(expected, actual.Raycast(ray, selection, out var actualHit));
            Assert.Equal(expectedHit, actualHit);
        }
    }
}
