using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
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
        AssertYaw(g.WorldPose.Orientation, 0x3e3cdd4fu, 0x3f7b9badu);
        Assert.Equal(new Vector3(0.23f, 0f, 0.17f), g.WorldPose.Position);
    }

    [Theory]
    [InlineData(0x3ebdf3b6u, 0x3e3cdd4fu, 0x3f7b9badu)]
    [InlineData(0xc0c6249bu, 0xbd3abf54u, 0xbf7fbbdau)]
    public void Yaw_IsTheDoubleHalfAngleSineAndCosineRoundedOnce(uint yawBits, uint sinBits, uint cosBits)
    {
        // The sine and cosine of half the yaw were taken in double outside the engine and rounded to float once. The
        // second yaw's half-angle sine is negative, so X and Z must still be positive zero.
        float yaw = BitConverter.UInt32BitsToSingle(yawBits);
        var g = MapPlacementShapes.Resolve(NativeWorldFixtures.Doorway(yaw, 1f).Resolved).Single(p => p.PlacementId == "doorway");
        AssertYaw(g.WorldPose.Orientation, sinBits, cosBits);
    }

    static void AssertYaw(Quaternion orientation, uint sinBits, uint cosBits) =>
        Assert.Equal((0u, sinBits, 0u, cosBits), (BitConverter.SingleToUInt32Bits(orientation.X),
            BitConverter.SingleToUInt32Bits(orientation.Y), BitConverter.SingleToUInt32Bits(orientation.Z),
            BitConverter.SingleToUInt32Bits(orientation.W)));

    [Fact]
    public void LowObject_EnvelopeIsSweptToOneMetreWhilePhysicalBoundsStay()
    {
        var g = MapPlacementShapes.Resolve(NativeWorldFixtures.Crate().Resolved).Single();
        Assert.Equal(0.2, g.ColliderBounds!.Value.MaxY - g.ColliderBounds.Value.MinY, 5);
        Assert.Equal(0.8f, g.Envelope.RaiseMetres, 5);
        Assert.Equal(1.0, g.Envelope.Bounds.MaxY - g.Envelope.Bounds.MinY, 5);
        Assert.Equal(g.ColliderBounds.Value.MinX, g.Envelope.Bounds.MinX, 5);
        Assert.Equal(g.ColliderBounds.Value.MaxZ, g.Envelope.Bounds.MaxZ, 5);
        Assert.Equal(g.ColliderBounds.Value.MinY, g.Envelope.Bounds.MinY, 5);
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

    [Fact]
    public void Digest_ChangesWithYawScaleAndNumericId()
    {
        static string Digest(NativeFixture f) =>
            MapPlacementShapes.Resolve(f.Resolved).Single(p => p.PlacementId == "doorway").Digest;
        string baseline = Digest(NativeWorldFixtures.Doorway(0.371f, 1.137f));
        Assert.NotEqual(baseline, Digest(NativeWorldFixtures.Doorway(0.372f, 1.137f)));
        Assert.NotEqual(baseline, Digest(NativeWorldFixtures.Doorway(0.371f, 1.138f)));
        Assert.NotEqual(baseline, Digest(NativeWorldFixtures.Doorway(0.371f, 1.137f, numericId: 2)));
        Assert.NotEqual(baseline, Digest(NativeWorldFixtures.Doorway(0.371f, 1.137f, numericId: null)));
    }

    [Fact]
    public void ShortCylinder_IsLengthenedFromItsBase()
    {
        var g = Single("short-post");
        Assert.Equal(0.0, g.ColliderBounds!.Value.MinY, 5);
        Assert.Equal(0.5, g.ColliderBounds.Value.MaxY, 5);
        Assert.Equal(0.5f, g.Envelope.RaiseMetres, 5);
        Assert.Equal(0.0, g.Envelope.Bounds.MinY, 5);
        Assert.Equal(1.0, g.Envelope.Bounds.MaxY, 5);
        var member = Assert.Single(((CompoundShape)g.Envelope.Shape).Children);
        Assert.Equal(1f, Assert.IsType<CylinderShape>(member.Shape).Length, 5);
        Assert.Equal(Vector3.Zero, member.Local.Position);
    }

    [Fact]
    public void UpsideDownCylinder_KeepsItsWorldBottom()
    {
        var g = Single("hanging-post");
        Assert.Equal(0.0, g.ColliderBounds!.Value.MinY, 5);
        Assert.Equal(0.5, g.ColliderBounds.Value.MaxY, 5);
        Assert.Equal(0.0, g.Envelope.Bounds.MinY, 5);
        Assert.Equal(1.0, g.Envelope.Bounds.MaxY, 5);
    }

    [Fact]
    public void TiltedCylinder_SweepsAHullThatContainsIt()
    {
        var g = Single("fallen-log");
        MapBox3 c = g.ColliderBounds!.Value, e = g.Envelope.Bounds;
        const double slack = 1e-5;
        Assert.True(e.MinX <= c.MinX + slack && e.MinY <= c.MinY + slack && e.MinZ <= c.MinZ + slack, $"{e} {c}");
        Assert.True(e.MaxX >= c.MaxX - slack && e.MaxY >= c.MaxY - slack && e.MaxZ >= c.MaxZ - slack, $"{e} {c}");
        Assert.Equal(c.MinY, e.MinY, 5);
        Assert.Equal(1.0, e.MaxY - e.MinY, 5);
        var member = Assert.Single(((CompoundShape)g.Envelope.Shape).Children);
        Assert.Equal(128, Assert.IsType<ConvexHullShape>(member.Shape).Points.Length);
    }

    [Fact]
    public void LowMesh_EnvelopeIsTheHullOfItsVerticesAndTheirRaise()
    {
        var g = Single("mesh-ramp");
        Assert.Equal(0.7f, g.Envelope.RaiseMetres, 5);
        var raise = new Vector3(0f, g.Envelope.RaiseMetres, 0f);
        Vector3[] vertices = NativeWorldFixtures.MeshRamp.Vertices;
        Assert.Equal(vertices.Concat(vertices.Select(v => v + raise)),
            Assert.IsType<ConvexHullShape>(g.Envelope.Shape).Points);
        Assert.Equal(g.ColliderBounds!.Value.MinY, g.Envelope.Bounds.MinY, 5);
        Assert.Equal(1.0, g.Envelope.Bounds.MaxY - g.Envelope.Bounds.MinY, 5);
    }

    [Fact]
    public void SelectionSource_IsNotDerivedFromCollider()
    {
        var g = Single("examine-sign");
        Assert.False(g.Envelope.DerivedFromCollider);
        Assert.Null(g.Collider);
        Assert.Null(g.ColliderBounds);
    }

    [Fact]
    public void TallObject_KeepsItsColliderAsTheEnvelope()
    {
        var g = MapPlacementShapes.Resolve(NativeWorldFixtures.Doorway(0.371f, 1.137f).Resolved)
            .Single(p => p.PlacementId == "doorway");
        Assert.Equal(0f, g.Envelope.RaiseMetres);
        Assert.Same(g.Collider, g.Envelope.Shape);
    }

    [Fact]
    public void SourceUnits_MultiplyThePlacementScale()
    {
        var g = MapPlacementShapes.Resolve(NativeWorldFixtures.Placed("centimetre-crate", scale: 2f).Resolved).Single();
        var box = Assert.IsType<BoxShape>(((CompoundShape)g.Collider!).Children[0].Shape);
        Assert.Equal(1f, box.HalfExtents.X, 5);
        Assert.Equal(1f, box.HalfExtents.Y, 5);
        Assert.Equal(1f, box.HalfExtents.Z, 5);
        Assert.Equal(2.0, g.ColliderBounds!.Value.MaxY - g.ColliderBounds.Value.MinY, 5);
    }

    [Fact]
    public void Policy_CanonicalTextAndHashArePinned()
    {
        Assert.Equal(
            "kemap/interaction-envelope/1\nminimumVerticalReachHeightMetres=1\nsource=selection-else-solid-collider\n" +
            "raise=world-vertical-sweep-of-members\nband=absolute-world-y-after-raise\n",
            MapInteractionPolicy.CanonicalText);
        Assert.Equal("eae5415fa47ef2fa08589c61a2742c34303ba5f963307ccc32d6ac660a5c147e", MapInteractionPolicy.Hash);
        Assert.Contains("\nminimumVerticalReachHeightMetres=" +
            MapInteractionPolicy.MinimumVerticalReachHeightMetres.ToString(CultureInfo.InvariantCulture) + "\n",
            MapInteractionPolicy.CanonicalText);
    }

    [Fact]
    public void UnsupportedShape_BoundsRefuseNamingTheType()
        => Assert.Contains(nameof(UnknownShape), Assert.Throws<MapDocumentException>(() =>
            MapShapeBounds.Of(new UnknownShape(), Pose.Identity)).Message);

    static MapPlacementGeometry Single(string assetId) =>
        MapPlacementShapes.Resolve(NativeWorldFixtures.Placed(assetId).Resolved).Single();

    sealed class UnknownShape : PhysicsShape { }
}
