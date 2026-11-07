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
        using var scene = new FramedTileScene(tileX, tileZ, origin, rimAlongX: !onX);
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

    [Theory]
    [InlineData(33554434, 0.125f, 4194304f, 0f, -0.0625f, true)]
    [InlineData(33554434, 0.125f, 4194304f, -0.0001f, -0.0625f, false)]
    [InlineData(33554434, 0.125f, 4194304f, 0.5001f, -0.0625f, false)]
    [InlineData(33554434, 0.125f, 4194304f, 0.25f, -0.1251f, false)]
    [InlineData(33554434, 0.125f, 4194304f, 0.25f, 0.0001f, false)]
    [InlineData(2, 1f, 0f, 2f, -0.5f, true)]
    [InlineData(2, 1f, 0f, 1.9999f, -0.5f, false)]
    [InlineData(2, 1f, 0f, 3.0001f, -0.5f, false)]
    public void OrdinaryPinPreservesRoundedColumnMembership(int tileX, float tileSize, float originX,
        float localX, float localZ, bool wet)
    {
        var field = MovementWaterFieldTests.SingleWaterTile(tileX, 0, tileSize);
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        Vector3 origin = new(originX, 0, 0);
        var extent = new OrdinaryMovementExtent(new(originX - 4, -4, -4), new(originX + 8, 4, 4), -4, -4);
        var builder = OrdinaryMovementRegistration.CreateBuilder("water-world", physics, extent);
        physics.Rebase(origin);
        OrdinaryMovementRegistration registration;
        using (var read = physics.AcquireQueryReadLease()) registration = builder.Build(read);
        using var binding = EffectiveMovementBinding.Ordinary(OrdinaryTileWaterTests.Tuning(WaterTraversalMode.SurfaceSwimmer),
            physics, registration, (_, _) => -4, null,
            MovementBoundary.Rectangle(originX - 4, -4, originX + 8, 4), field);
        var frame = new MovementFrameDescriptor(default, origin, 1);
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var acquired));
        using var lease = Assert.IsType<MovementQueryLease>(acquired);
        Vector3 centre = new(localX, -1, localZ);
        var state = new FramedMovementState(new MoveState { Position = centre }, frame, null);
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(state, out var selection));
        var body = new MovementBodyQuery(centre, 0.03125f, 0.75f, selection.Space, null);
        if (wet)
        {
            var spans = new MovementCoverageSpan[64];
            var contacts = new MovementDomainContact[256];
            var trace = lease.TraceWater(new(body, Vector3.Zero), spans, contacts);
            Assert.Equal(MovementAvailability.Known, trace.Availability);
            Assert.True(trace.ContactsWritten > 0);
            Assert.All(contacts.Take(trace.ContactsWritten), contact =>
                Assert.Equal(MovementContactOverlap.Overlapping, contact.SpanOverlap));
        }
        var point = lease.SampleCentreWater(body);
        Assert.Equal(MovementAvailability.Known, point.Availability);
        Assert.Equal(wet, point.InWater);
        if (wet)
        {
            Assert.Equal(-4f, point.Interval!.Value.LowerY);
            Assert.Equal(0f, point.Interval.Value.UpperY);
        }
        else
        {
            Assert.Null(point.Interval);
            Assert.Null(point.Domain);
        }
    }

    sealed class FramedTileScene : IDisposable
    {
        readonly BepuPhysicsWorld _physics = new(Vector3.Zero);
        readonly TileColliderRegistration _installed;
        internal EffectiveMovementBinding Binding { get; }
        internal MovementFrameDescriptor Frame { get; }

        internal FramedTileScene(int tileX, int tileZ, Vector3 origin, bool rimAlongX = false)
        {
            var region = RegionCoord.Of(tileX, tileZ);
            var doc = TileWorldPhysicsTestData.FlatWorld(region);
            for (int z = region.OriginZ; z < region.OriginZ + TileRegion.Size; z++)
                for (int x = region.OriginX; x < region.OriginX + TileRegion.Size; x++)
                    doc.SetCornerHeightCm(x, z, 0, -400);
            doc.SetUnderlay(tileX, tileZ, 0, TileWorldPhysicsTestData.Water);
            // The connected rim is on the untested axis, preserving both original faces under test.
            int rimX = tileX + (rimAlongX ? 1 : 0), rimZ = tileZ + (rimAlongX ? 0 : 1);
            doc.SetUnderlay(rimX, rimZ, 0, TileWorldPhysicsTestData.Water);
            doc.SetCornerHeightCm(rimX + 1, rimZ + 1, 0, 2);
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
