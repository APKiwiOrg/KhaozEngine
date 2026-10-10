using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>How a resolved placement's collider, world pose and interaction envelope are derived from its asset
/// shapes, and which placements are refused.</summary>
public class MapPlacementGeometryTests
{
    [Fact]
    public void Collider_ScalesBySourceUnitsAndPlacementScaleOnce()
    {
        var f = NativeWorldFixtures.Doorway(yaw: 0.371f, scale: 1.137f);
        var g = MapPlacementShapes.Resolve(f.Resolved).Single(p => p.PlacementId == "doorway");
        var jamb = ((CompoundShape)g.Collider!).Children[0];
        Assert.Equal(new Vector3(0.15f, 1.2f, 0.15f) * 1.137f, ((BoxShape)jamb.Shape).HalfExtents);
        Assert.Equal(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.371f), g.WorldPose.Orientation);
        Assert.Equal(new Vector3(0.23f, 0f, 0.17f), g.WorldPose.Position);
    }

    [Fact]
    public void LowObject_EnvelopeIsSweptToOneMetreWhilePhysicalBoundsStay()
    {
        var g = MapPlacementShapes.Resolve(NativeWorldFixtures.Crate().Resolved).Single();
        Assert.Equal(0.2, g.ColliderBounds!.Value.MaxY - g.ColliderBounds.Value.MinY, 5);
        Assert.Equal(0.8f, g.Envelope.RaiseMetres, 5);
        Assert.Equal(1.0, g.Envelope.Bounds.MaxY - g.Envelope.Bounds.MinY, 5);
        Assert.Equal(g.ColliderBounds.Value.MinX, g.Envelope.Bounds.MinX, 5);
        Assert.Equal(g.ColliderBounds.Value.MaxZ, g.Envelope.Bounds.MaxZ, 5);
    }

    [Fact]
    public void SolidWithoutSelection_DerivesEnvelopeFromCollider()
        => Assert.True(MapPlacementShapes.Resolve(NativeWorldFixtures.Crate().Resolved).Single().Envelope.DerivedFromCollider);

    [Fact]
    public void NonSolidWithoutSelection_Refuses()
        => Assert.Contains("interaction source", Assert.Throws<MapDocumentException>(() =>
            MapPlacementShapes.Resolve(NativeWorldFixtures.ShapelessProp().Resolved)).Message);

    [Fact]
    public void SlopeSeatedAndCornerWalls_KeepTheirShapes()
    {
        var f = NativeWorldFixtures.SlopeAndCornerWalls();
        var shapes = MapPlacementShapes.Resolve(f.Resolved);
        var seated = shapes.Single(p => p.PlacementId == "slope-wall");
        Assert.Equal(f.SlopeHeightAtWall, seated.ColliderBounds!.Value.MinY, 4);
        var corner = shapes.Single(p => p.PlacementId == "corner-wall");
        Assert.Equal(2, ((CompoundShape)corner.Collider!).Children.Length);
    }

    [Fact]
    public void TwoResolutions_HaveIdenticalDigests()
        => Assert.Equal(
            MapPlacementShapes.Resolve(NativeWorldFixtures.Doorway(0.371f, 1.137f).Resolved).Select(p => p.Digest),
            MapPlacementShapes.Resolve(NativeWorldFixtures.Doorway(0.371f, 1.137f).Resolved).Select(p => p.Digest));
}
