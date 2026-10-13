using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class OrdinaryTileWaterFrameTests
{
    // Each row puts the exact centre a fraction of a binary32 step from a one-metre water tile's lateral face,
    // where the rounded absolute sum lands on the other side of that face.
    [Theory]
    [InlineData("x-outside-min")]
    [InlineData("x-inside-max")]
    [InlineData("z-inside-min")]
    [InlineData("z-outside-max")]
    public void LateralTileFaceMembershipIsExactAtALossyOrigin(string row)
    {
        bool onX = row.StartsWith('x');
        int tileX = onX ? 257 : 0, tileZ = onX ? 0 : 256;
        Vector3 origin = onX ? new(256, 0, 0) : new(0, 0, -256);
        Vector3 centre = row switch
        {
            "x-outside-min" => new(MathF.BitDecrement(1f), -1, -0.5f),
            "x-inside-max" => new(MathF.BitDecrement(2f), -1, -0.5f),
            "z-inside-min" => new(0.5f, -1, MathF.BitIncrement(-1f)),
            _ => new(0.5f, -1, MathF.BitIncrement(0f))
        };
        // The tile covers world X [x, x + 1) and world Z (-(z + 1), -z]. Local faces are exact integers here.
        float minX = tileX - origin.X, maxX = tileX + 1 - origin.X, minZ = -(tileZ + 1) - origin.Z, maxZ = -tileZ - origin.Z;
        bool exact = centre.X >= minX && centre.X < maxX && centre.Z > minZ && centre.Z <= maxZ;
        Vector3 rounded = centre + origin;
        bool roundedWet = rounded.X >= tileX && rounded.X < tileX + 1 && rounded.Z > -(tileZ + 1) && rounded.Z <= -tileZ;
        Assert.NotEqual(exact, roundedWet);
        using var scene = new FramedTileScene(tileX, tileZ, origin);
        using var lease = scene.Acquire();
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(scene.State(centre), out var selection));
        var point = lease.SampleCentreWater(new(centre, 0.25f, 0.75f, selection.Space, null));
        Assert.Equal(MovementAvailability.Known, point.Availability);
        Assert.Equal(exact, point.InWater);
        if (!exact)
        {
            Assert.Null(point.Interval);
            return;
        }
        Assert.Equal(-4f - origin.Y, point.Interval!.Value.LowerY);
        Assert.Equal(0f - origin.Y, point.Interval!.Value.UpperY);
    }

    [Fact]
    public void TraceAtOrigin256CertifiesTheExactFaceCrossing()
    {
        using var scene = new FramedTileScene(257, 0, new(256, 0, 0));
        using var lease = scene.Acquire();
        Vector3 centre = new(0.1f, -1, -0.5f);
        Assert.NotEqual((double)centre.X + 256, (double)(centre.X + 256f));
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(scene.State(centre), out var selection));
        var body = new MovementBodyQuery(centre, 0.25f, 0.75f, selection.Space, null);
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var result = lease.TraceWater(new(body, Vector3.UnitX), spans, contacts);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.InRange(result.CertifiedErrorMetres, 0f, MovementQueryLease.CoverageSkinMetres);
        Assert.True(result.ContactsWritten > 0);
        // The capsule reaches the face at world X 257 when its centre is at 256.75.
        double crossing = 0.75 - (double)centre.X;
        float first = contacts.Take(result.ContactsWritten).Min(contact => contact.Fraction);
        Assert.Equal((float)crossing, first);
        Assert.NotEqual((float)(0.75 - (double)(centre.X + 256f) + 256), first);
        MovementDomainContact entry = contacts.Take(result.ContactsWritten).First(contact => contact.Fraction == first);
        Assert.Equal(1f, entry.IntervalColumnXZ.X);
        Assert.Equal(-4f, entry.Interval.LowerY);
        Assert.Equal(0f, entry.Interval.UpperY);
        var dry = spans.Take(result.SpansWritten).Where(span => span.HasDryCoverage && span.ContactCount == 0).ToArray();
        Assert.Contains(dry, span => span.EnterFraction == 0 && span.ExitFraction <= crossing);
    }

    sealed class FramedTileScene : IDisposable
    {
        readonly BepuPhysicsWorld _physics = new(Vector3.Zero);
        readonly TileColliderRegistration _installed;
        internal EffectiveMovementBinding Binding { get; }
        internal MovementFrameDescriptor Frame { get; }

        internal FramedTileScene(int tileX, int tileZ, Vector3 origin)
        {
            var region = RegionCoord.Of(tileX, tileZ);
            var doc = TileWorldPhysicsTestData.FlatWorld(region);
            doc.SetUnderlay(tileX, tileZ, 0, TileWorldPhysicsTestData.Water);
            doc.SetCornerHeightCm(tileX, tileZ, 0, -400);
            doc.SetCornerHeightCm(tileX + 1, tileZ, 0, 2);
            var colliders = TileWorldColliders.Build(doc, TileWorldPhysicsTestData.Catalogs(), "water-world");
            _installed = colliders.AddTo(_physics);
            _physics.Rebase(origin);
            Binding = EffectiveMovementBinding.Ordinary(OrdinaryTileWaterTests.Tuning(WaterTraversalMode.SurfaceSwimmer),
                _physics, _installed.CreateMovementRegistration("water-world"), colliders.MovementGroundHeight,
                colliders.MovementGroundNormal, MovementBoundary.Rectangle(region.OriginX, -(region.OriginZ + TileRegion.Size),
                    region.OriginX + TileRegion.Size, -region.OriginZ), colliders.Medium);
            Frame = new(default, _physics.Origin, 1);
        }

        internal MovementQueryLease Acquire()
        {
            Assert.Equal(MovementAvailability.Known, Binding.TryAcquire(Binding.CreateOrdinaryScope(Frame), out var lease));
            return Assert.IsType<MovementQueryLease>(lease);
        }

        internal FramedMovementState State(Vector3 centre) =>
            new(new MoveState { Position = centre, Grounded = false }, Frame, null);

        public void Dispose() { Binding.Dispose(); _installed.Dispose(); _physics.Dispose(); }
    }
}
