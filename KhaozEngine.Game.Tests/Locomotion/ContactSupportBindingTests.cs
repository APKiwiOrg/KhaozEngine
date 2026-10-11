using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class ContactSupportBindingTests
{
    static readonly MovementSpaceKey Room = new("world", "room");
    static readonly MovementSupportKey Bank = new("world", "bank");
    static readonly MovementQueryIdentity OtherIdentity = new("closure", 1, "other-scope");
    static readonly SupportSample Sample = new(SupportStatus.Walkable, 0.25f, 0.00001f,
        Vector3.UnitY, new StaticHandle(1), 3, new(1, 0.25f, 0));

    [Fact]
    public void B3FootprintBindsBankBeforeAxisReachesItsEdgeWithoutMovingTheSample()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new(8, 0.125f, 8)), Pose.At(new(0, -0.125f, 0)));
        StaticHandle bank = world.AddStatic(new BoxShape(new(2, 0.125f, 4)), Pose.At(new(3, 0.125f, 0)));
        using var view = world.CreateQueryViewExcludingStatics([]);
        SupportSample sample;
        using (var physics = world.AcquireQueryReadLease())
            sample = FootSupport.Find(null, null, view, physics, new(new(0.9f, 0), 0, 0.125f, 0.4f, 0, 0.7f));
        Assert.Equal(SupportStatus.Walkable, sample.Status);
        Assert.InRange(sample.Height, 0.24995f, 0.25005f);
        Assert.Equal(bank, sample.Static);
        Assert.True(sample.Witness.X >= 1);
        var environment = new EnvironmentAcquisitionFixture(view) { ContactSupportEnabled = true };
        environment.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding binding) =>
        {
            Assert.Equal(new Vector2(0.9f, 0), request.Axis);
            Assert.Equal(sample.Height, request.Height);
            Assert.Equal(sample.HeightError, request.HeightError);
            Assert.Equal(sample.Normal, request.Normal);
            Assert.Equal(sample.Witness, request.Witness);
            Assert.Equal(bank, request.Static);
            Assert.Equal(sample.FeatureId, request.FeatureId);
            binding = new(Bank, Room, null, environment.Identity);
            return MovementAvailability.Known;
        };
        using var lease = Acquire(environment);
        Assert.Equal(MovementAvailability.Known, lease.BindContactSupport(Request(0.9f), sample, out var result));
        Assert.Equal(Bank, result.Owner);
        Assert.Equal(Room, result.Space);
    }

    [Theory]
    [InlineData("stale-pin")]
    [InlineData("geometry-generation")]
    [InlineData("frame")]
    [InlineData("foreign-view")]
    public void Q1StaleOrForeignBindingRefusesBeforeProducerAccess(string fault)
    {
        using var scene = new Scene();
        AssertKnown(scene);
        int before = scene.Environment.ContactSupportCalls;
        if (fault == "foreign-view") scene.Environment.PinView = scene.OtherView;
        else scene.Environment.Fault = fault;
        Assert.Equal(MovementAvailability.Stale, scene.Lease.BindContactSupport(Request(), Sample, out var binding));
        Assert.Equal(default, binding);
        Assert.Equal(before, scene.Environment.ContactSupportCalls);
    }

    [Fact]
    public void Q1DisposedLeaseCannotPublishBinding()
    {
        using var scene = new Scene();
        AssertKnown(scene);
        scene.Lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => scene.Lease.BindContactSupport(Request(), Sample, out _));
        Assert.Equal(1, scene.Environment.ContactSupportCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Q1CurrentnessIsRecheckedAfterEachProducerOperation(bool analytic)
    {
        using var scene = new Scene();
        AssertKnown(scene);
        scene.Environment.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding binding) =>
        {
            scene.Environment.Fault = "stale-pin";
            binding = new(Bank, Room, null, scene.Environment.Identity);
            return MovementAvailability.Known;
        };
        scene.Environment.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
        {
            scene.Environment.Fault = "stale-pin";
            ground = Ground(scene.Environment.Identity);
            return MovementAvailability.Known;
        };
        if (analytic)
        {
            Assert.Equal(MovementAvailability.Stale, scene.Lease.SampleAnalyticGround(Request(), out var ground));
            Assert.Null(ground);
        }
        else
        {
            Assert.Equal(MovementAvailability.Stale, scene.Lease.BindContactSupport(Request(), Sample, out var binding));
            Assert.Equal(default, binding);
        }
    }

    [Theory]
    [InlineData("rise")]
    [InlineData("drop")]
    [InlineData("origin")]
    [InlineData("probe-margin")]
    [InlineData("signal-horizon")]
    [InlineData("pushed-axis")]
    public void Q2UncertifiedReachOrAxisRefusesBeforeEitherProducerCall(string fault)
    {
        using var scene = new Scene();
        AssertKnown(scene);
        MovementSupportRequest request = fault switch
        {
            "rise" => Request(rise: 0.6f),
            "drop" => Request(drop: 3),
            "origin" => Request(start: new(8, 0, 0)),
            "probe-margin" => Request(x: 3.99f, radius: 0.005f, halfHeight: 0.02f),
            "signal-horizon" => Request(x: 5.8f),
            _ => Request(x: -5)
        };
        int before = scene.Environment.ContactSupportCalls;
        Assert.Equal(MovementAvailability.Unresolved, scene.Lease.SampleAnalyticGround(request, out var ground));
        Assert.Null(ground);
        Assert.Equal(MovementAvailability.Unresolved, scene.Lease.BindContactSupport(request, Sample, out var binding));
        Assert.Equal(default, binding);
        Assert.Equal(0, scene.Environment.AnalyticGroundCalls);
        Assert.Equal(before, scene.Environment.ContactSupportCalls);
    }

    [Fact]
    public void Q3CorrectionRebuildsWithoutPredictedOwnerBeforeContactQueries()
    {
        using var scene = new Scene(cold: true);
        var destination = new MovementSpaceKey("world", "destination");
        scene.Environment.OnRebuild = (in FramedMovementState state, out MovementSelection selection) =>
        {
            Assert.Null(state.Selection);
            selection = new(destination, null, scene.Environment.Identity);
            return MovementAvailability.Known;
        };
        Assert.Equal(MovementAvailability.Unresolved, scene.Lease.BindContactSupport(Request(), Sample, out _));
        var corrected = new FramedMovementState(new MoveState { Position = new(0, 0.75f, 0), Grounded = true },
            scene.Lease.Frame, new(Room, Bank, scene.Environment.Identity));
        Assert.Equal(MovementAvailability.Known, scene.Lease.RebuildSelection(corrected, out var selection));
        Assert.Equal(destination, selection.Space);
        Assert.Null(selection.Support);
        scene.Environment.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding binding) =>
        {
            Assert.Equal(destination, request.Transition.OriginSpace);
            binding = new(new("world", "destination-floor"), destination, null, scene.Environment.Identity);
            return MovementAvailability.Known;
        };
        Assert.Equal(MovementAvailability.Known,
            scene.Lease.BindContactSupport(Request(space: destination), Sample, out var result));
        Assert.Equal("destination-floor", result.Owner.LocalId);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("space")]
    [InlineData("identity")]
    [InlineData("world")]
    [InlineData("missing-owner")]
    [InlineData("missing-link")]
    public void Q4ContradictoryOrUnboundOwnerCannotPublishAResult(string fault)
    {
        using var scene = new Scene();
        AssertKnown(scene);
        scene.Environment.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding binding) =>
        {
            binding = fault switch
            {
                "owner" => new(new("world", "other-owner"), Room, null, scene.Environment.Identity),
                "space" => new(Bank, new("world", "other-room"), "portal", scene.Environment.Identity),
                "identity" => new(Bank, Room, null, OtherIdentity),
                "world" => new(new("other-world", "bank"), new("other-world", "room"), null, scene.Environment.Identity),
                "missing-link" => new(Bank, new("world", "other-room"), null, scene.Environment.Identity),
                _ => default
            };
            return fault == "missing-owner" ? MovementAvailability.Unresolved : MovementAvailability.Known;
        };
        Assert.Equal(fault == "missing-owner" ? MovementAvailability.Unresolved : MovementAvailability.Invalid,
            scene.Lease.BindContactSupport(Request(), Sample, out var result));
        Assert.Equal(default, result);
    }

    [Fact]
    public void AnalyticAbsenceIsKnownAndDoesNotInventGround()
    {
        using var scene = new Scene();
        AssertKnown(scene);
        Assert.Equal(MovementAvailability.Known, scene.Lease.SampleAnalyticGround(Request(), out var ground));
        Assert.Null(ground);
    }

    [Fact]
    public void AnalyticFactsStayScopedToTheRoomAndExactAxis()
    {
        using var scene = new Scene();
        scene.Environment.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
        {
            ground = Ground(scene.Environment.Identity);
            return MovementAvailability.Known;
        };
        Assert.Equal(MovementAvailability.Known, scene.Lease.SampleAnalyticGround(Request(), out var ground));
        Assert.Equal(0.25f, ground!.Value.Height);
        Assert.Equal(MovementAvailability.Invalid,
            scene.Lease.SampleAnalyticGround(Request(space: new("world", "unrelated-room")), out ground));
        Assert.Null(ground);
        Assert.Equal(MovementAvailability.Invalid, scene.Lease.SampleAnalyticGround(Request(x: 0.5f), out ground));
        Assert.Null(ground);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedAnalyticReadCannotChangeHeightOrKnownAbsence(bool absent)
    {
        using var scene = new Scene();
        scene.Environment.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
        {
            ground = absent ? null : Ground(scene.Environment.Identity);
            return MovementAvailability.Known;
        };
        Assert.Equal(MovementAvailability.Known, scene.Lease.SampleAnalyticGround(Request(), out _));
        scene.Environment.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
        {
            ground = new(new(0, 0), 0.5f, Vector3.UnitY, Bank, Room, null, scene.Environment.Identity);
            return MovementAvailability.Known;
        };
        Assert.Equal(MovementAvailability.Invalid, scene.Lease.SampleAnalyticGround(Request(), out var result));
        Assert.Null(result);
    }

    [Fact]
    public void MissingOptionalCapabilityExplicitlyRefusesBothQueries()
    {
        using var scene = new Scene();
        AssertKnown(scene);
        using var otherWorld = new BepuPhysicsWorld(Vector3.Zero);
        using var view = otherWorld.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view);
        using var lease = Acquire(environment);
        Assert.Equal(MovementAvailability.Unresolved, lease.SampleAnalyticGround(Request(), out var ground));
        Assert.Null(ground);
        Assert.Equal(MovementAvailability.Unresolved, lease.BindContactSupport(Request(), Sample, out var binding));
        Assert.Equal(default, binding);
    }

    static MovementAnalyticGround Ground(MovementQueryIdentity identity) =>
        new(new(0, 0), 0.25f, Vector3.UnitY, Bank, Room, null, identity);

    [Fact]
    public void AnalyticBindingCannotReplaceTheOwnerOfTheCertifiedField()
    {
        using var scene = new Scene();
        scene.Environment.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
        {
            ground = Ground(scene.Environment.Identity);
            return MovementAvailability.Known;
        };
        Assert.Equal(MovementAvailability.Known, scene.Lease.SampleAnalyticGround(Request(), out _));
        var sample = Sample with { Static = null, FeatureId = -1, HeightError = 0, Witness = new(0, 0.25f, 0) };
        Assert.Equal(MovementAvailability.Known, scene.Lease.BindContactSupport(Request(), sample, out _));
        scene.Environment.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding binding) =>
        {
            binding = new(new("world", "invented-floor"), Room, null, scene.Environment.Identity);
            return MovementAvailability.Known;
        };
        Assert.Equal(MovementAvailability.Invalid, scene.Lease.BindContactSupport(Request(), sample, out var result));
        Assert.Equal(default, result);
    }

    [Fact]
    public void OutOfReachCertifiedSampleIsInvalidBeforeOwnerLookup()
    {
        using var scene = new Scene();
        AssertKnown(scene);
        var sample = Sample with { Height = 2, Witness = new(1, 2, 0) };
        Assert.Equal(MovementAvailability.Invalid, scene.Lease.BindContactSupport(Request(), sample, out var result));
        Assert.Equal(default, result);
        Assert.Equal(1, scene.Environment.ContactSupportCalls);
    }

    [Fact]
    public void RepeatedAnalyticFieldRetainsGeometryWhenTheLegalOriginChanges()
    {
        using var scene = new Scene();
        scene.Environment.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
        {
            ground = new(Vector2.Zero, 0.25f, Vector3.UnitY, Bank, Room,
                request.Transition.OriginSpace == Room ? null : "portal", scene.Environment.Identity);
            return MovementAvailability.Known;
        };
        var body = Request().Body;
        var crossing = new MovementSupportRequest(body, 0.4f, 0, 0.8f,
            new(new("world", "origin-room"), new(-1, 0, 0)));
        Assert.Equal(MovementAvailability.Known, scene.Lease.SampleAnalyticGround(crossing, out var crossed));
        Assert.Equal("portal", crossed!.Value.TraversedLinkId);
        Assert.Equal(MovementAvailability.Known, scene.Lease.SampleAnalyticGround(Request(), out var settled));
        Assert.Null(settled!.Value.TraversedLinkId);
        Assert.Equal(0.25f, settled.Value.Height);
        Assert.Equal(Bank, settled.Value.Owner);
    }

    static void AssertKnown(Scene scene) => Assert.Equal(MovementAvailability.Known,
        scene.Lease.BindContactSupport(Request(), Sample, out _));

    static MovementSupportRequest Request(float x = 0, float rise = 0.4f, float drop = 0,
        Vector3? start = null, float radius = 0.25f, float halfHeight = 0.75f, MovementSpaceKey? space = null)
    {
        var body = new MovementBodyQuery(new(x, halfHeight, 0), radius, halfHeight, space ?? Room, null);
        return new(body, rise, drop, 0.8f, new(space ?? Room, start ?? body.Feet));
    }

    static MovementQueryLease Acquire(EnvironmentAcquisitionFixture environment)
    {
        var result = environment.Acquire();
        Assert.Equal(MovementAvailability.Known, result.Status);
        return Assert.IsType<MovementQueryLease>(result.Lease);
    }

    sealed class Scene : IDisposable
    {
        readonly BepuPhysicsWorld world = new(Vector3.Zero);
        readonly IPhysicsWorldQueryView view;
        public IPhysicsWorldQueryView OtherView { get; }
        public EnvironmentAcquisitionFixture Environment { get; }
        public MovementQueryLease Lease { get; }
        public Scene(bool cold = false)
        {
            view = world.CreateQueryViewExcludingStatics([]);
            OtherView = world.CreateQueryViewExcludingStatics([]);
            MovementQueryScope? scope = cold ? new(new(-4), new(4), 0.5f, 2, "world", null,
                new("closure", 1, "scope"), new(WorldFrame.Origin, Vector3.Zero, 1)) : null;
            Environment = new(view, scope) { ContactSupportEnabled = true };
            Environment.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding binding) =>
            {
                binding = new(Bank, Room, null, Environment.Identity);
                return MovementAvailability.Known;
            };
            Lease = Acquire(Environment);
        }
        public void Dispose() { Lease.Dispose(); OtherView.Dispose(); view.Dispose(); world.Dispose(); }
    }
}
