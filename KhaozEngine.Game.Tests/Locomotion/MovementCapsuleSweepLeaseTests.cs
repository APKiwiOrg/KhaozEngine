using System;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// Consumer acceptance only. Scripted complete values do not prove backend geometry or numeric bounds.
public class MovementCapsuleSweepLeaseTests
{
    static readonly MovementSpaceKey Room = new("world", "room");
    static readonly MovementBodyQuery Body = new(new Vector3(0.5f, 0.75f, -0.25f), 0.3f, 0.75f, Room, null);

    [Fact]
    public void ClearForwardsTheActualCapsuleCentreThroughTheSelectedViewUnderTheReadGate()
    {
        using var scene = new Scene();
        scene.Script.Result = Clear(1f);
        scene.Script.OnSweep = () => Assert.Throws<InvalidOperationException>(() => scene.World.Step(1f / 30f));
        Capture result = Query(scene.Lease, Vector3.UnitX);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(scene.Script.Result, result.Result);
        Assert.Equal(Body.Centre, scene.Script.ObservedPose.Position);
        Assert.Equal(Quaternion.Identity, scene.Script.ObservedPose.Orientation);
        CapsuleShape capsule = Assert.IsType<CapsuleShape>(scene.Script.ObservedCapsule);
        Assert.Equal(Body.Radius, capsule.Radius);
        Assert.Equal(2f * (Body.HalfHeight - Body.Radius), capsule.Length);
        Assert.Equal(Vector3.UnitX, scene.Script.ObservedDisplacement);
        Assert.Equal(default, scene.Script.ObservedFilter);
        Assert.Equal(1, scene.Script.Calls);
        Assert.Equal(0, scene.View.LegacySweepCalls);
    }

    [Fact]
    public void HitInsideTheRequestIsAcceptedAsDataWithoutChangingThePose()
    {
        using var scene = new Scene();
        scene.Script.Result = new(CapsuleSweepStatus.Hit, 0.25f, 0.3f, 0.001f);
        Capture result = Query(scene.Lease, Vector3.UnitX);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(scene.Script.Result, result.Result);
        Assert.Equal(Body.Centre, scene.Script.ObservedPose.Position);
    }

    [Fact]
    public void StationaryClearAndZeroHitRemainDistinctAcceptedData()
    {
        using var scene = new Scene();
        scene.Script.Result = Clear(0f);
        Capture clear = Query(scene.Lease, Vector3.Zero);
        Assert.Equal(MovementAvailability.Known, clear.Availability);
        Assert.Equal(CapsuleSweepStatus.Clear, clear.Result.Status);
        scene.Script.Result = new(CapsuleSweepStatus.Hit, 0f, 0f, 0f);
        Capture hit = Query(scene.Lease, Vector3.Zero);
        Assert.Equal(MovementAvailability.Known, hit.Availability);
        Assert.Equal(CapsuleSweepStatus.Hit, hit.Result.Status);
        Assert.Equal(0f, hit.Result.ClearThroughDistance);
        Assert.Equal(0f, hit.Result.ImpactDistance);
    }

    [Fact]
    public void ShortClearCannotPublishAPrefix()
    {
        using var scene = new Scene();
        scene.Script.Result = Clear(0.99f);
        AssertRefused(Query(scene.Lease, Vector3.UnitX), MovementAvailability.Invalid);
    }

    [Fact]
    public void OverlongClearCannotClaimAnotherRequest()
    {
        using var scene = new Scene();
        scene.Script.Result = Clear(1.01f);
        AssertRefused(Query(scene.Lease, Vector3.UnitX), MovementAvailability.Invalid);
    }

    [Fact]
    public void HitUpperBoundCannotExceedTheRequest()
    {
        using var scene = new Scene();
        scene.Script.Result = new(CapsuleSweepStatus.Hit, 0.9f, 1.1f, 0f);
        AssertRefused(Query(scene.Lease, Vector3.UnitX), MovementAvailability.Invalid);
    }

    [Fact]
    public void BothHitBoundsBeyondTheRequestAreRejected()
    {
        using var scene = new Scene();
        scene.Script.Result = new(CapsuleSweepStatus.Hit, 1.1f, 1.2f, 0f);
        AssertRefused(Query(scene.Lease, Vector3.UnitX), MovementAvailability.Invalid);
    }

    [Fact]
    public void NextRepresentableErrorAboveTheCapIsUnresolved()
    {
        using var scene = new Scene();
        scene.Script.Result = new(CapsuleSweepStatus.Clear, 1f, null, MathF.BitIncrement(0.001f));
        AssertRefused(Query(scene.Lease, Vector3.UnitX), MovementAvailability.Unresolved);
    }

    [Fact]
    public void ErrorExactlyAtTheCapPassesTheConsumerGuard()
    {
        using var scene = new Scene();
        scene.Script.Result = new(CapsuleSweepStatus.Clear, 1f, null, 0.001f);
        Capture result = Query(scene.Lease, Vector3.UnitX);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(scene.Script.Result, result.Result);
    }

    [Fact]
    public void DefaultBackendResultRemainsUnresolved()
    {
        using var scene = new Scene();
        AssertRefused(Query(scene.Lease, Vector3.UnitX), MovementAvailability.Unresolved);
    }

    [Fact]
    public void MissingOptionalCapabilityDoesNotUseTheLegacySweep()
    {
        using var scene = new Scene(capability: false);
        AssertRefused(Query(scene.Lease, Vector3.UnitX), MovementAvailability.Unresolved);
        Assert.Equal(0, scene.View.LegacySweepCalls);
    }

    [Fact]
    public void SweptCapsuleOutsideCertifiedScopeRefusesBeforeBackendAccess()
    {
        using var scene = new Scene();
        scene.Script.Result = Clear(3.8f);
        AssertRefused(Query(scene.Lease, new Vector3(3.8f, 0f, 0f)), MovementAvailability.Unresolved);
        Assert.Equal(0, scene.Script.Calls);
    }

    [Fact]
    public void ColdSelectionRefusesBeforeBackendAccess()
    {
        using var scene = new Scene(cold: true);
        scene.Script.Result = Clear(1f);
        AssertRefused(Query(scene.Lease, Vector3.UnitX), MovementAvailability.Unresolved);
        Assert.Equal(0, scene.Script.Calls);
    }

    [Fact]
    public void AlreadyStalePinPreventsBackendAccess()
    {
        using var scene = new Scene();
        scene.Environment.Fault = "stale-pin";
        AssertRefused(Query(scene.Lease, Vector3.UnitX), MovementAvailability.Stale);
        Assert.Equal(0, scene.Script.Calls);
    }

    [Fact]
    public void PinBecomingStaleDuringTheCallDiscardsTheCompleteResult()
    {
        using var scene = new Scene();
        scene.Script.Result = Clear(1f);
        scene.Script.OnSweep = () => scene.Environment.Fault = "stale-pin";
        AssertRefused(Query(scene.Lease, Vector3.UnitX), MovementAvailability.Stale);
        Assert.Equal(1, scene.Script.Calls);
    }

    [Fact]
    public void UnsupportedBackendDomainLeavesNoResultAndLeaseCanBeReleased()
    {
        using var scene = new Scene();
        scene.Script.OnSweep = () => throw new NotSupportedException("Outside scripted domain.");
        AssertRefused(Query(scene.Lease, Vector3.UnitX), MovementAvailability.Unresolved);
        scene.Lease.AssertCurrent();
        scene.Lease.Dispose();
        scene.World.Step(1f / 30f);
    }

    static CapsuleSweepResult Clear(float distance) => new(CapsuleSweepStatus.Clear, distance, null, 0f);

    static void AssertRefused(Capture capture, MovementAvailability availability)
    {
        Assert.Equal(availability, capture.Availability);
        Assert.Equal(default, capture.Result);
    }

    static Capture Query(MovementQueryLease lease, Vector3 displacement)
    {
        MethodInfo? method = typeof(MovementQueryLease).GetMethod("QuerySolidSweep",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        object?[] args = [Body, displacement, Clear(123f)];
        try
        {
            var availability = (MovementAvailability)method.Invoke(lease, args)!;
            return new Capture(availability, (CapsuleSweepResult)args[2]!);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    readonly record struct Capture(MovementAvailability Availability, CapsuleSweepResult Result);

    sealed class Scene : IDisposable
    {
        public readonly BepuPhysicsWorld World = new(Vector3.Zero);
        public readonly SweepQueryView View;
        public readonly EnvironmentAcquisitionFixture Environment;
        public readonly MovementQueryLease Lease;
        public ScriptedSweepQueryView Script => (ScriptedSweepQueryView)View;

        public Scene(bool capability = true, bool cold = false)
        {
            StaticHandle excluded = World.AddStatic(new BoxShape(Vector3.One), Pose.At(new Vector3(20f)));
            IPhysicsWorldQueryView selected = World.CreateQueryViewExcludingStatics([excluded]);
            View = capability ? new ScriptedSweepQueryView(selected) : new SweepQueryView(selected);
            var identity = new MovementQueryIdentity("closure", 1u, "scope");
            var frame = new MovementFrameDescriptor(WorldFrame.Origin, World.Origin, 1ul);
            var scope = new MovementQueryScope(new Vector3(-4f), new Vector3(4f), 0.5f, 2f,
                "world", cold ? null : Room, identity, frame);
            Environment = new EnvironmentAcquisitionFixture(View, scope);
            var acquired = Environment.Acquire();
            Assert.Equal(MovementAvailability.Known, acquired.Status);
            Lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
        }

        public void Dispose()
        {
            try { Lease.Dispose(); }
            finally
            {
                try { View.Dispose(); }
                finally { World.Dispose(); }
            }
        }
    }
}
