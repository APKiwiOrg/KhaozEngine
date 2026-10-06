using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// Located-fact and consumer-coherence tests. Scripted producer facts do not prove continuous slope clipping.
public class MovementEnvironmentColumnTests
{
    static readonly MovementSpaceKey Room = new("world", "room");
    static readonly MovementDomainKey Water = new("world", "water");
    static readonly MovementQueryIdentity Identity = new("closure", 1u, "scope");
    const float Radius = 0.25f;
    const float HalfHeight = 0.75f;

    [Fact]
    public void SlopedFloorFactsNameTheirActualColumnInBothTravelDirections()
    {
        foreach (float direction in new[] { -1f, 1f })
        {
            using var scene = new Scene();
            var query = new MovementMediumSweepQuery(Body(new Vector3(-direction, 3f, 0f)), Vector3.UnitX * (2f * direction));
            Vector2 first = new(-direction, 0f), last = new(direction, 0f);
            MovementDomainContact a = Contact(new(0.5f * first.X - 2f, 5f, 5f, true, "slope", "surface"), first, 0f);
            MovementDomainContact b = Contact(new(0.5f * last.X - 2f, 5f, 5f, true, "slope", "surface"), last, 1f);
            Capture result = scene.Read(query, a, b);
            Assert.Equal(MovementAvailability.Known, result.Result.Availability);
            Assert.Equal(first, Column(result.Contacts[0]));
            Assert.Equal(last, Column(result.Contacts[1]));
            Assert.Equal(direction, result.Contacts[1].Interval.LowerY - result.Contacts[0].Interval.LowerY);
            Assert.Equal(1f, result.Contacts[1].Fraction);
        }
    }

    [Fact]
    public void SlopedCeilingFactsKeepTheirLocalUpperBoundInBothTravelDirections()
    {
        foreach (float direction in new[] { -1f, 1f })
        {
            using var scene = new Scene();
            var query = new MovementMediumSweepQuery(Body(new Vector3(-direction, -1f, 0f)), Vector3.UnitX * (2f * direction));
            Vector2 first = new(-direction, 0f), last = new(direction, 0f);
            MovementDomainContact a = Contact(new(-5f, 0.5f * first.X + 1f, 5f, false, "floor", "sloped-ceiling"), first, 0f);
            MovementDomainContact b = Contact(new(-5f, 0.5f * last.X + 1f, 5f, false, "floor", "sloped-ceiling"), last, 1f);
            Capture result = scene.Read(query, a, b);
            Assert.Equal(MovementAvailability.Known, result.Result.Availability);
            Assert.Equal(first, Column(result.Contacts[0]));
            Assert.Equal(last, Column(result.Contacts[1]));
            Assert.Equal(direction, result.Contacts[1].Interval.UpperY - result.Contacts[0].Interval.UpperY);
            Assert.All(result.Contacts, contact => { Assert.False(contact.Interval.UpperIsFreeSurface); Assert.Equal(5f, contact.Interval.NominalSurfaceY); });
        }
    }

    [Fact]
    public void FloodedOuterColumnCanBeWetWhileCentreMembershipIsDry()
    {
        using var scene = new Scene();
        MovementBodyQuery body = Body(new Vector3(0f, 0.75f, 0f));
        scene.Environment.Point = new(MovementAvailability.Known, Room, null, false, 1f, null);
        Assert.False(scene.Lease.SampleCentreWater(body).InWater);
        Vector2 column = new(0.2f, 0f);
        MovementDomainContact contact = Contact(new(-4f, 1f, 10f, false, "floor", "ceiling"), column, 0f);
        Capture result = scene.Read(new(body, Vector3.Zero), contact);
        Assert.Equal(MovementAvailability.Known, result.Result.Availability);
        Assert.Equal(column, Column(result.Contacts[0]));
        Assert.False(result.Contacts[0].Interval.UpperIsFreeSurface);
        Assert.Equal(1f, result.Contacts[0].Interval.UpperY);
        Assert.Equal(10f, result.Contacts[0].Interval.NominalSurfaceY);
    }

    [Fact]
    public void SameXzButWrongVerticalIntervalCannotPublishEitherBuffer()
    {
        using var scene = new Scene();
        MovementDomainContact wrong = Contact(new(-4f, -3f, -3f, true, "floor", "surface"), Vector2.Zero, 0f);
        AssertInvalidAtomic(scene, new(Body(new Vector3(0f, 0.75f, 0f)), Vector3.Zero), wrong);
        Assert.Equal(1, scene.Environment.CoverageCalls);
    }

    [Fact]
    public void DeepIntervalOutsideQueryYIsValidWhenItsLocalCapsuleSliceOverlaps()
    {
        using var scene = new Scene();
        MovementDomainContact deep = Contact(new(-1000f, 1f, 1f, true, "deep-bed", "surface"), Vector2.Zero, 0f);
        Capture result = scene.Read(new(Body(new Vector3(0f, 0.75f, 0f)), Vector3.Zero), deep);
        Assert.Equal(MovementAvailability.Known, result.Result.Availability);
        Assert.True(deep.Interval.LowerY < scene.Lease.Witness.Scope.EnvelopeMin.Y);
        Assert.Equal(deep, result.Contacts[0]);
    }

    [Fact]
    public void NonfiniteAndUncertifiedColumnsAreRejectedWithoutPublication()
    {
        MovementWaterInterval interval = new(-4f, 1f, 1f, true, "floor", "surface");
        Assert.Throws<ArgumentException>(() => Contact(interval, new Vector2(float.NaN, 0f), 0f));
        Assert.Throws<ArgumentException>(() => Contact(interval, new Vector2(0f, float.PositiveInfinity), 0f));
        using var scene = new Scene();
        MovementDomainContact outside = Contact(interval, new Vector2(100f, 0f), 0f);
        AssertInvalidAtomic(scene, new(Body(new Vector3(0f, 0.75f, 0f)), Vector3.Zero), outside);
        Assert.Equal(1, scene.Environment.CoverageCalls);
    }

    [Fact]
    public void RebindingRequeriesColumnAndIntervalYUnderAFreshPin()
    {
        var oldFrame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1ul);
        var state = new FramedMovementState(new MoveState { Position = new Vector3(0f, 0.75f, 0f) }, oldFrame, null);
        Vector3 origin = new(2f, 3f, -1f);
        var newFrame = new MovementFrameDescriptor(WorldFrame.Nearest(origin), origin, 2ul);
        Assert.True(MovementFrameRebinding.TryRebind(state, newFrame, out FramedMovementState rebound));
        Assert.Null(rebound.Selection);
        MovementBodyQuery originalBody = Body(state.State.Position);
        Assert.Equal(state.State.Position, originalBody.Centre);
        MovementDomainContact original = Contact(new(-4f, 1f, 1f, true, "floor", "surface"), Vector2.Zero, 0f, 7u);
        using (var old = new Scene())
            Assert.Equal(MovementAvailability.Known,
                old.Read(new(originalBody, Vector3.Zero), original).Result.Availability);

        using var scene = new Scene(origin, cold: true);
        scene.Environment.OnRebuild = (in FramedMovementState _, out MovementSelection selection) =>
        {
            selection = new MovementSelection(Room, null, Identity);
            return MovementAvailability.Known;
        };
        Assert.Equal(MovementAvailability.Known, scene.Lease.RebuildSelection(rebound, out _));
        Vector2 column = new(-origin.X, -origin.Z);
        MovementDomainContact fresh = Contact(new(-4f - origin.Y, 1f - origin.Y, 1f - origin.Y, true, "floor", "surface"), column, 0f, 99u);
        MovementBodyQuery reboundBody = Body(rebound.State.Position);
        Assert.Equal(rebound.State.Position, reboundBody.Centre);
        Assert.Equal(originalBody.Centre - origin, reboundBody.Centre);
        var query = new MovementMediumSweepQuery(reboundBody, Vector3.Zero);
        Capture result = scene.Read(query, fresh);
        Assert.Equal(MovementAvailability.Known, result.Result.Availability);
        Assert.Equal(Identity, result.Result.Identity);
        Assert.Equal(column, Column(result.Contacts[0]));
        Assert.Equal(original.Interval.LowerY - origin.Y, result.Contacts[0].Interval.LowerY);
        Assert.Equal(original.Interval.UpperY - origin.Y, result.Contacts[0].Interval.UpperY);
        Assert.NotEqual(original.CoverageRegionHandle, result.Contacts[0].CoverageRegionHandle);
        MovementDomainContact staleColumn = Contact(fresh.Interval, Vector2.Zero, 0f, 99u);
        AssertInvalidAtomic(scene, query, staleColumn);
    }

    [Fact]
    public void RoundedSliceRejectsDiscOnlyOverlapButRetainsClosedSkinTangency()
    {
        using var scene = new Scene();
        var query = new MovementMediumSweepQuery(Body(new Vector3(0f, 0.75f, 0f)), Vector3.Zero);
        Vector2 edge = new(Radius + 0.001f, 0f);
        // At the inflated disc's rim the cylindrical Y interval is [0.25,1.25], not [0,1.5].
        MovementDomainContact outsideCap = Contact(new(0f, 0.1f, 0.1f, true, "floor", "surface"), edge, 0f);
        AssertInvalidAtomic(scene, query, outsideCap);
        MovementDomainContact tangent = Contact(new(1.25f, 2f, 2f, true, "floor", "surface"), edge, 0f);
        Capture result = scene.Read(query, tangent);
        Assert.Equal(MovementAvailability.Known, result.Result.Availability);
        Assert.Equal(tangent, result.Contacts[0]);
    }

    static void AssertInvalidAtomic(Scene scene, MovementMediumSweepQuery query, MovementDomainContact invalid)
    {
        MovementCoverageSpan span = new(0.5f, 1f, 0, 1, true);
        MovementDomainContact sentinel = Contact(new(-4f, 1f, 1f, true, "sentinel-low", "sentinel-high"), Vector2.Zero, 0.75f, 123u);
        MovementCoverageSpan[] spans = [span];
        MovementDomainContact[] contacts = [sentinel];
        scene.SetContacts(invalid);
        MovementCoverageResult result = scene.Lease.TraceWater(query, spans, contacts);
        Assert.Equal(MovementAvailability.Invalid, result.Availability);
        Assert.Equal(span, spans[0]);
        Assert.Equal(sentinel, contacts[0]);
    }

    sealed record Capture(MovementCoverageResult Result, MovementDomainContact[] Contacts);
    sealed class Scene : IDisposable
    {
        readonly BepuPhysicsWorld _world = new(Vector3.Zero);
        readonly IPhysicsWorldQueryView _view;
        public EnvironmentAcquisitionFixture Environment { get; }
        public MovementQueryLease Lease { get; }
        public Scene(Vector3 origin = default, bool cold = false)
        {
            if (origin != Vector3.Zero) _world.Rebase(origin);
            _view = _world.CreateQueryViewExcludingStatics([]);
            var frame = new MovementFrameDescriptor(WorldFrame.Nearest(origin), origin, cold ? 2ul : 1ul);
            var scope = new MovementQueryScope(new Vector3(-4f) - origin, new Vector3(4f) - origin,
                0f, 0f, "world", cold ? null : Room, Identity, frame);
            Environment = new EnvironmentAcquisitionFixture(_view, scope);
            var acquired = Environment.Acquire();
            Assert.Equal(MovementAvailability.Known, acquired.Status);
            Lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
        }

        public void SetContacts(params MovementDomainContact[] facts)
        {
            Environment.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
            {
                spans[0] = new MovementCoverageSpan(0f, 1f, 0, facts.Length, true);
                facts.AsSpan().CopyTo(contacts);
                return new MovementCoverageResult(MovementAvailability.Known, 1, 1, facts.Length, facts.Length, 0.00001f, Identity);
            };
        }

        public Capture Read(MovementMediumSweepQuery query, params MovementDomainContact[] facts)
        {
            SetContacts(facts);
            var spans = new MovementCoverageSpan[1];
            var contacts = new MovementDomainContact[facts.Length];
            MovementCoverageResult result = Lease.TraceWater(query, spans, contacts);
            return new Capture(result, contacts[..result.ContactsWritten]);
        }
        public void Dispose() { Lease.Dispose(); _view.Dispose(); _world.Dispose(); }
    }

    static MovementBodyQuery Body(Vector3 centre) => new(centre, Radius, HalfHeight, Room, null);

    static MovementDomainContact Contact(MovementWaterInterval interval, Vector2 column, float fraction, uint handle = 1u) =>
        new(Water, Room, interval, column, Vector3.UnitY, fraction, "boundary", handle);

    static Vector2 Column(MovementDomainContact contact) => contact.IntervalColumnXZ;
}
