using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Physics;
using KhaozEngine.Tests.MapDoc.Prefabs;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed partial class MapPresentationGeometryParityTests
{
    [Fact]
    public void ReleasedSyntheticShapes_FreezeBeforeExtraction()
    {
        Assert.Equal(PresentationGeometryFixtures.Frozen, PresentationGeometryFixtures.Queries().Select(q => PresentationGeometryFixtures.Snapshot(q)));
    }

    [Fact]
    public void GenericShapeExtraction_PreservesReleasedQueries()
    {
        Assert.Equal(PresentationGeometryFixtures.Frozen,
            PresentationGeometryFixtures.Queries().Select(q => PresentationGeometryFixtures.Snapshot(q, generic: true)));
        foreach (var query in PresentationGeometryFixtures.Queries())
            Assert.Equal(query.Pose.Orientation, PropQueryGeometry.RaiseEnvelope(query.Shape, query.Pose, 1).Pose.Orientation);
    }

    [Theory]
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)]
    public void GenericRay_NormalizesFiniteDoubleDirections(double direction)
    {
        Assert.True(ShapeRayQueries.Cast(new(-3, 0, 0, direction, 0, 0, 10),
            new BoxShape(Vector3.One), Pose.Identity, out var hit));
        Assert.Equal(new ShapeRayHit(2, -Vector3.UnitX, false), hit);
    }

    [Theory]
    [InlineData(1.99, false)]
    [InlineData(3, true)]
    public void GenericRay_SubnormalSquaredDirectionKeepsMetreLimits(double maximumDistance, bool expectedHit)
    {
        bool found = ShapeRayQueries.Cast(new(-3, 0, 0, 1e-161, 0, 0, maximumDistance),
            new BoxShape(Vector3.One), Pose.Identity, out var hit);
        Assert.Equal(expectedHit, found);
        if (expectedHit) Assert.Equal(new ShapeRayHit(2, -Vector3.UnitX, false), hit);
        else Assert.Equal(default(ShapeRayHit), hit);
    }

    [Fact]
    public void GenericRay_PreservesDoubleOriginInsideFlagsAndFirstTie()
    {
        var box = new BoxShape(new(.25f));
        Assert.True(ShapeRayQueries.Cast(new(32001.0000009536743164, 0, 0, -2, 0, 0, 2),
            box, Pose.At(new(32000, 0, 0)), out var precise));
        Assert.Equal(.7500009536743164, precise.Distance);
        Assert.Equal(Vector3.UnitX, precise.Normal);
        Assert.False(precise.Inside);
        Assert.True(ShapeRayQueries.Cast(new(0, 0, 0, 0, 0, 1, 0), box, Pose.Identity, out var inside));
        Assert.Equal(new ShapeRayHit(0, -Vector3.UnitZ, true), inside);
        var compound = new CompoundShape(new[]
        {
            new CompoundChild(new BoxShape(Vector3.One), Pose.Identity),
            new CompoundChild(new TriangleMeshShape(
                new Vector3[] { new(-1, -1, -2), new(1, -1, 0), new(0, 1, -1) }, new[] { 0, 1, 2 }), Pose.Identity),
        });
        Assert.True(ShapeRayQueries.Cast(new(0, 0, -3, 0, 0, 1, 10), compound, Pose.Identity, out var tied));
        Assert.Equal(new ShapeRayHit(2, -Vector3.UnitZ, false), tied);
        Assert.False(ShapeRayQueries.Cast(new(0, 0, -3, 0, 0, 1, 1), box, Pose.Identity, out _));
    }

    [Fact]
    public void SharedExtraction_PreservesMapWrapperExceptionCategories()
    {
        var unsupported = new UnknownShape();
        var box = new BoxShape(Vector3.One);
        Assert.Throws<ArgumentNullException>(() => MapShapeQueries.Raycast(default, null!, Pose.Identity, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => MapShapeQueries.Raycast(default, box, Pose.Identity, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShapeRayQueries.Cast(new(0, 0, 0, 0, 0, 0, 1), box, Pose.Identity, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShapeRayQueries.Cast(new(double.NaN, 0, 0, 1, 0, 0, 1), box, Pose.Identity, out _));
        Assert.Throws<NotSupportedException>(() => ShapeRayQueries.Cast(new(0, 0, 0, 1, 0, 0, 1), new SphereShape(1), Pose.Identity, out _));
        Assert.Throws<MapDocumentException>(() => MapShapeBounds.Of(unsupported, Pose.Identity));
        Assert.Throws<MapDocumentException>(() => MapShapeBounds.Of(new ConvexHullShape([]), Pose.Identity));
        Assert.Throws<MapDocumentException>(() => MapInteractionEnvelope.Build("sphere", new SphereShape(.1f), false, Pose.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() => PropQueryGeometry.RaiseEnvelope(box, Pose.Identity, float.NaN));
    }

    sealed class UnknownShape : PhysicsShape { }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ObserverRoom_HidesOnlyLinkedRoofAboveThreshold(bool tiled, bool buffered)
    {
        var fixture = PrefabFixtures.LinkedBuildings();
        using var native = new NativeSceneFixture(fixture.Document, fixture.Assets);
        var scene = NativeSceneFixtures.Open(native, tiled).Acquire(Scope()).Scene!;
        var buffer = MapRoofEvaluationBuffer.Prepare(scene).Buffer!;
        var retained = buffer.Roofs;
        MapRoofIntentResult Evaluate(Vector3 local, MapRoofMode mode = MapRoofMode.Auto)
        {
            var point = new MapFramePoint(scene.SurfaceView.Scope.Frame, local);
            if (!buffered) return MapRoofPolicy.Evaluate(scene, point, mode);
            buffer.Evaluate(point, mode);
            Assert.Same(retained, buffer.Roofs);
            return new(buffer.Status, buffer.SpaceId, buffer.Roofs, scene.SurfaceView.ReadWitness);
        }
        var inside = Evaluate(new(1, 1, -1));
        Assert.Equal(MapMembershipStatus.Resolved, inside.Status);
        Assert.Equal("room-a", inside.SpaceId);
        Assert.Equal(new[] { "roof-a", "roof-b", "shelter-roof" }, inside.Roofs.Select(r => r.RoofPlacementId));
        Assert.All(inside.Roofs.Where(r => r.RoofPlacementId != "shelter-roof"), Hidden);
        Visible(inside.Roofs.Single(r => r.RoofPlacementId == "shelter-roof"));
        Assert.All(Evaluate(new(1, 3, -1)).Roofs, Visible);
        Assert.All(Evaluate(new(9, 1, -9)).Roofs, Visible);
        Assert.All(Evaluate(new(1, 1, -1), MapRoofMode.AlwaysVisible).Roofs, Visible);
        Assert.All(Evaluate(new(9, 1, -9), MapRoofMode.AlwaysHidden).Roofs, Hidden);
        var missing = Evaluate(new(100, 1, -1));
        Assert.All(missing.Roofs, Visible);
        Assert.Equal(MapMembershipStatus.MissingGeometry, missing.Status);
    }

    [Fact]
    public void AmbiguousObserver_PolicyAndBufferKeepEveryRoofVisible()
    {
        var fixture = PrefabFixtures.Cave("peer-overlap");
        using var native = new NativeSceneFixture(fixture.Document, fixture.Assets);
        var acquired = NativeSceneFixtures.Open(native, false).Acquire(Scope());
        Assert.True(acquired.Witness.CompleteForScope, string.Join("\n", acquired.Findings));
        var scene = acquired.Scene!;
        var point = new MapFramePoint(scene.SurfaceView.Scope.Frame, new(2, 1, -2));
        var result = MapRoofPolicy.Evaluate(scene, point, MapRoofMode.Auto);
        Assert.Equal(MapMembershipStatus.Ambiguous, result.Status);
        Assert.NotEmpty(result.Roofs);
        Assert.All(result.Roofs, Visible);
        var buffer = MapRoofEvaluationBuffer.Prepare(scene).Buffer!;
        buffer.Evaluate(point, MapRoofMode.Auto);
        Assert.Equal(MapMembershipStatus.Ambiguous, buffer.Status);
        Assert.Null(buffer.SpaceId);
        Assert.Equal(result.Roofs, buffer.Roofs);
    }

    [Fact]
    public void RoofModes_LeaveUnlinkedShelterAndCaveCeilingIndependent()
    {
        var fixture = PrefabFixtures.Village();
        var resources = PrefabFixtures.Effective(fixture);
        using var native = new NativeSceneFixture(fixture.Document, fixture.Assets);
        var scope = Scope();
        scope = scope with
        {
            DrawBounds = new(0, -32, -32, 40, 32, 0),
            Surfaces = scope.Surfaces with { LocalMin = new(0, -32), LocalMax = new(40, 0) }
        };
        var acquired = NativeSceneFixtures.Open(native, false).Acquire(scope);
        Assert.True(acquired.Witness.CompleteForScope, string.Join("\n", acquired.Findings));
        var scene = acquired.Scene!;
        var ceiling = scene.SurfaceView.Witness.Present.Where(p => p.Key.SurfaceId == "cave-ceiling").ToArray();
        Assert.NotEmpty(ceiling);
        var buffer = MapRoofEvaluationBuffer.Prepare(scene).Buffer!;
        var point = new MapFramePoint(scene.SurfaceView.Scope.Frame, new(37, -4, -21));
        var detached = MapRoofPolicy.Evaluate(scene, point, MapRoofMode.Auto);
        Assert.Equal("village-cave", detached.SpaceId);
        Assert.All(detached.Roofs, Visible);
        Assert.DoesNotContain(detached.Roofs, r => r.RoofPlacementId == "cave-ceiling");
        var retainedRows = buffer.Roofs;
        foreach (var mode in new[] { MapRoofMode.AlwaysHidden, MapRoofMode.AlwaysVisible, MapRoofMode.Auto })
        {
            buffer.Evaluate(point, mode);
            Assert.Same(retainedRows, buffer.Roofs);
            Assert.Equal(ceiling, scene.SurfaceView.Witness.Present.Where(p => p.Key.SurfaceId == "cave-ceiling"));
            var full = MapRoofPolicy.Evaluate(resources, resources.CompleteView(),
                new(resources.CompleteView().Scope.Frame, point.Local), mode);
            Assert.Equal(full.Roofs, buffer.Roofs);
        }
        Assert.All(detached.Roofs, Visible);
        Assert.Same(scene.SurfaceView.ReadWitness, detached.Witness);
    }

    static MapSceneScope Scope() => new(
        new(KhaozEngine.Primitives.WorldFrame.Origin, new(-32, -32), new(32, 32), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, new()),
        new(-32, -32, -32, 32, 32, 32), MapSceneChannels.All);

    static void Hidden(MapRoofIntent roof)
    {
        Assert.False(roof.ColourVisible);
        Assert.True(roof.CastShadows);
        Assert.False(roof.CameraBoomBlocks);
        Assert.True(roof.SkyPickingBlocks);
    }

    static void Visible(MapRoofIntent roof)
    {
        Assert.True(roof.ColourVisible);
        Assert.True(roof.CastShadows);
        Assert.True(roof.CameraBoomBlocks);
        Assert.True(roof.SkyPickingBlocks);
    }
}
