using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class MovementMediumFactTests
{
    static readonly MovementSpaceKey Room = new("world", "room");
    static readonly MovementDomainKey Lake = new("world", "lake");
    static readonly MovementBodyQuery Body = new(new(0, 0.75f, 0), 0.25f, 0.75f, Room, null);
    static MovementWaterInterval Interval(float level) => new(-1, level, level, true, "bed", "surface");
    static MovementDomainContact Contact(float level, MovementDomainKey? domain = null, Vector2 column = default) =>
        new(domain ?? Lake, Room, Interval(level), column, Vector3.UnitX, 0, "edge", 1, MovementContactOverlap.Overlapping);
    static MovementWaterPoint Point(float level) => new(MovementAvailability.Known, Room, Lake, true, 1, Interval(level));

    [Fact]
    public void OneTraceCannotGiveTheSameBodyTwoNominalLevels()
    {
        using var scene = new Scene();
        scene.Contacts = [Contact(1), Contact(2)];
        AssertInvalidAtomic(scene);
    }

    [Fact]
    public void TraceMustAgreeWithAWetPointAlreadyReadUnderTheLease()
    {
        using var scene = new Scene();
        scene.Environment.Point = Point(1);
        Assert.Equal(MovementAvailability.Known, scene.Lease.SampleCentreWater(Body).Availability);
        scene.Contacts = [Contact(2)];
        AssertInvalidAtomic(scene);
    }

    [Fact]
    public void WetPointMustAgreeWithAnEarlierTrace()
    {
        using var scene = new Scene();
        scene.Contacts = [Contact(1)];
        Assert.Equal(MovementAvailability.Known, scene.Trace().Availability);
        scene.Environment.Point = Point(2);
        var refused = scene.Lease.SampleCentreWater(Body);
        Assert.Equal(MovementAvailability.Invalid, refused.Availability);
        Assert.False(refused.InWater);
        Assert.Null(refused.Interval);
    }

    [Fact]
    public void SeparateLevelBodiesRemainIndependent()
    {
        using var scene = new Scene();
        scene.Contacts = [Contact(1, column: new(-0.1f, 0)), Contact(2, new("world", "other"), new(0.1f, 0))];
        Assert.Equal(MovementAvailability.Known, scene.Trace().Availability);
    }

    [Fact]
    public void ARefusedTraceDoesNotRegisterAPartialLevelFact()
    {
        using var scene = new Scene();
        scene.Contacts = [Contact(2), Contact(2, column: new(100, 0))];
        AssertInvalidAtomic(scene);
        scene.Environment.Point = Point(1);
        Assert.Equal(MovementAvailability.Known, scene.Lease.SampleCentreWater(Body).Availability);
    }

    [Fact]
    public void TangentTagCannotHideAColumnDeepInsideTheUninflatedCapsule()
    {
        using var scene = new Scene();
        scene.Contacts = [Tagged(Contact(1), "Tangent")];
        AssertInvalidAtomic(scene);
    }

    [Fact]
    public void SkinOnlyTangencyRemainsAKnownContact()
    {
        using var scene = new Scene();
        scene.Contacts = [Tagged(Contact(1, column: new(0.251f, 0)), "Tangent")];
        Assert.Equal(MovementAvailability.Known, scene.Trace().Availability);
    }

    [Fact]
    public void PositiveOverlapRemainsKnownWhenExplicitlyClassified()
    {
        using var scene = new Scene();
        scene.Contacts = [Tagged(Contact(1), "Overlapping")];
        Assert.Equal(MovementAvailability.Known, scene.Trace().Availability);
    }

    [Fact]
    public void DistinctLevelFactsHaveABoundedLeaseLifetime()
    {
        using var scene = new Scene();
        scene.Environment.OnBodySample = (in MovementBodyQuery body) => new(MovementAvailability.Known, Room,
            new MovementDomainKey("world", "body-" + (int)(body.Centre.X * 128)), true, 1, Interval(1));
        for (int i = 0; i < 256; i++)
        {
            var body = new MovementBodyQuery(new(i / 128f, 0.75f, 0), 0.25f, 0.75f, Room, null);
            Assert.Equal(MovementAvailability.Known, scene.Lease.SampleCentreWater(body).Availability);
        }
        var last = new MovementBodyQuery(new(2, 0.75f, 0), 0.25f, 0.75f, Room, null);
        Assert.Equal(MovementAvailability.CapacityExceeded, scene.Lease.SampleCentreWater(last).Availability);
        Assert.Equal(MovementAvailability.Known, scene.Lease.SampleCentreWater(Body).Availability);
    }

    static MovementDomainContact Tagged(MovementDomainContact value, string tag)
    {
        Type? overlap = typeof(MovementDomainContact).Assembly.GetType("KhaozEngine.Locomotion.MovementContactOverlap");
        Assert.NotNull(overlap);
        ConstructorInfo? constructor = typeof(MovementDomainContact).GetConstructor(new[]
        {
            typeof(MovementDomainKey), typeof(MovementSpaceKey), typeof(MovementWaterInterval), typeof(Vector2),
            typeof(Vector3), typeof(float), typeof(string), typeof(uint), overlap
        });
        Assert.NotNull(constructor);
        return (MovementDomainContact)constructor.Invoke(new object[]
        {
            value.Domain, value.Space, value.Interval, value.IntervalColumnXZ, value.Normal, value.Fraction,
            value.BoundaryId, value.CoverageRegionHandle, Enum.Parse(overlap, tag)
        });
    }

    static void AssertInvalidAtomic(Scene scene)
    {
        var sentinel = Contact(3);
        var span = new MovementCoverageSpan(0, 1, 0, 0, true);
        MovementCoverageSpan[] spans = [span];
        MovementDomainContact[] contacts = [sentinel, sentinel];
        var result = scene.Lease.TraceWater(new(Body, Vector3.Zero), spans, contacts);
        Assert.Equal(MovementAvailability.Invalid, result.Availability);
        Assert.Equal(0, result.SpansWritten);
        Assert.Equal(span, spans[0]);
        Assert.All(contacts, contact => Assert.Equal(sentinel, contact));
    }

    sealed class Scene : IDisposable
    {
        readonly BepuPhysicsWorld world = new(Vector3.Zero);
        readonly KhaozEngine.Physics.IPhysicsWorldQueryView view;
        public EnvironmentAcquisitionFixture Environment { get; }
        public MovementQueryLease Lease { get; }
        public MovementDomainContact[] Contacts = [];
        public Scene()
        {
            view = world.CreateQueryViewExcludingStatics([]);
            Environment = new(view);
            Environment.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> spans,
                Span<MovementDomainContact> contacts) =>
            {
                spans[0] = new(0, 1, 0, Contacts.Length, true);
                Contacts.AsSpan().CopyTo(contacts);
                return new(MovementAvailability.Known, 1, 1, Contacts.Length, Contacts.Length, 0.00001f, Environment.Identity);
            };
            var acquired = Environment.Acquire();
            Assert.Equal(MovementAvailability.Known, acquired.Status);
            Lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
        }
        public MovementCoverageResult Trace() => Lease.TraceWater(new(Body, Vector3.Zero),
            new MovementCoverageSpan[64], new MovementDomainContact[256]);
        public void Dispose() { Lease.Dispose(); view.Dispose(); world.Dispose(); }
    }
}
