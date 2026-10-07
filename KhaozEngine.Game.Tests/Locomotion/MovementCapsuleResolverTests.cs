using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// The solid stage returns a tentative polyline. It cannot commit movement or certify water policy.
public partial class MovementCapsuleResolverTests
{
    static readonly MovementSpaceKey Room = new("world", "room");
    static readonly Vector3 Sentinel = new(91, 92, 93);
    static MovementBodyQuery Body(float height = 0.75f) => new(new(0, height + 0.002f, 0), 0.25f, height, Room, null);

    [Fact]
    public void EmptyWorldReturnsTheActualCentreEndpoint()
    {
        using var scene = new Scene();
        Capture result = scene.Resolve(Body(), new(3, 0, 1));
        Assert.Equal(MovementAvailability.Known, result.Status);
        Assert.False(result.Blocked);
        Assert.Equal(Body().Centre + new Vector3(3, 0, 1), result.End);
        AssertCertifiedSegments(scene, Body(), result);
    }

    [Fact]
    public void ZeroDisplacementStillProvesTheStartingCapsule()
    {
        using var scene = new Scene();
        Capture result = scene.Resolve(Body(), Vector3.Zero);
        Assert.Equal(MovementAvailability.Known, result.Status);
        Assert.Equal(Body().Centre, result.End);
        Assert.False(result.Blocked);
        Assert.Contains(scene.View.Calls, call => call.Delta == Vector3.Zero);
    }

    [Theory]
    [InlineData(0.4f)]
    [InlineData(0.75f)]
    public void ThinInteriorWallStopsBothCapsuleProfiles(float height)
    {
        using var scene = new Scene();
        scene.WallX();
        Capture result = scene.Resolve(Body(height), new(4, 0, 0));
        AssertBlocked(result);
        Assert.InRange(result.End.X, 1.70f, 1.71875f);
        Assert.Equal(Body(height).Centre.Y, result.End.Y);
        AssertCertifiedSegments(scene, Body(height), result);
    }

    [Fact]
    public void WallSlideRetainsTheUnblockedComponentAndRetracesIt()
    {
        using var scene = new Scene();
        scene.WallX();
        Capture result = scene.Resolve(Body(), new(4, 0, 2));
        AssertBlocked(result);
        Assert.InRange(result.End.X, 1.70f, 1.71875f);
        Assert.InRange(result.End.Z, 1.999f, 2.001f);
        Assert.True(result.Count >= 2);
        AssertCertifiedSegments(scene, Body(), result);
    }

    [Fact]
    public void BedCannotHideTheLaterWallWhileSliding()
    {
        using var scene = new Scene();
        scene.Floor();
        scene.WallX();
        Capture result = scene.Resolve(Body(), new(4, -0.5f, 2));
        AssertBlocked(result);
        Assert.InRange(result.End.X, 1.70f, 1.71875f);
        Assert.InRange(result.End.Y, 0.75f, 0.7521f);
        Assert.InRange(result.End.Z, 1.999f, 2.001f);
        AssertCertifiedSegments(scene, Body(), result);
    }

    [Fact]
    public void CeilingClipsAscentWithoutDiscardingHorizontalMovement()
    {
        using var scene = new Scene();
        scene.World.AddStatic(new BoxShape(new(8, 0.125f, 8)), Pose.At(new(0, 2.125f, 0)));
        Capture result = scene.Resolve(Body(), new(2, 2, 0));
        AssertBlocked(result);
        Assert.InRange(result.End.Y, 1.23f, 1.25f);
        Assert.InRange(result.End.X, 1.999f, 2.001f);
        AssertCertifiedSegments(scene, Body(), result);
    }

    [Fact]
    public void TiedCornerConstraintsAreIndependentOfInsertionOrder()
    {
        using var a = new Scene();
        using var b = new Scene();
        a.WallX(); a.WallZ();
        b.WallZ(); b.WallX();
        Capture first = a.Resolve(Body(), new(4, 0, 4));
        Capture second = b.Resolve(Body(), new(4, 0, 4));
        AssertBlocked(first);
        AssertBlocked(second);
        Assert.Equal(first.End, second.End);
        Assert.Equal(first.Path.AsSpan(0, first.Count).ToArray(), second.Path.AsSpan(0, second.Count).ToArray());
        Assert.Contains(a.View.ContactSets, set => Array.Exists(set, n => n.X < -0.99f) &&
            Array.Exists(set, n => n.Z < -0.99f));
        Assert.InRange(first.End.X, 1.70f, 1.71875f);
        Assert.InRange(first.End.Z, 1.70f, 1.71875f);
        AssertCertifiedSegments(a, Body(), first);
        AssertCertifiedSegments(b, Body(), second);
    }

    [Fact]
    public void SeparatedTangentTravelAlongAWallRemainsClear()
    {
        using var scene = new Scene();
        scene.WallX();
        var body = new MovementBodyQuery(new(1.717f, 0.752f, 0), 0.25f, 0.75f, Room, null);
        Capture result = scene.Resolve(body, new(0, 0, 2));
        Assert.Equal(MovementAvailability.Known, result.Status);
        Assert.False(result.Blocked);
        Assert.Equal(body.Centre + new Vector3(0, 0, 2), result.End);
        AssertCertifiedSegments(scene, body, result);
    }

    [Fact]
    public void ASelectedViewExclusionIsPreservedByEveryTrace()
    {
        using var scene = new Scene();
        StaticHandle wall = scene.WallX();
        scene.Excluded = [wall];
        Capture result = scene.Resolve(Body(), new(4, 0, 0));
        Assert.Equal(MovementAvailability.Known, result.Status);
        Assert.False(result.Blocked);
        Assert.Equal(4f, result.End.X);
        AssertCertifiedSegments(scene, Body(), result);
    }

    [Fact]
    public void ARealDynamicBoxParticipatesInTheSameSolver()
    {
        using var scene = new Scene();
        scene.World.AddDynamic(new BoxShape(new(0.03125f, 3, 8)), Pose.At(new(2, 0, 0)),
            new DynamicBodyDescription(0));
        Capture result = scene.Resolve(Body(), new(4, 0, 0));
        AssertBlocked(result);
        Assert.InRange(result.End.X, 1.70f, 1.71875f);
        AssertCertifiedSegments(scene, Body(), result);
    }

    [Fact]
    public void MirroredApproachPreservesTheSameClearance()
    {
        using var scene = new Scene();
        scene.WallX(-2);
        Capture result = scene.Resolve(Body(), new(-4, 0, 0));
        AssertBlocked(result);
        Assert.InRange(result.End.X, -1.71875f, -1.70f);
        AssertCertifiedSegments(scene, Body(), result);
    }

    static void AssertBlocked(Capture result)
    {
        Assert.Equal(MovementAvailability.Known, result.Status);
        Assert.True(result.Blocked);
        Assert.InRange(result.Count, 1, 9);
    }

    static void AssertCertifiedSegments(Scene scene, MovementBodyQuery body, Capture result)
    {
        Vector3 start = body.Centre;
        foreach (Vector3 end in result.Path.AsSpan(0, result.Count))
        {
            Vector3 expectedStart = start;
            Assert.Contains(scene.View.Calls, call => call.Status == CapsuleSweepStatus.Clear &&
                call.Centre == expectedStart && call.Centre + call.Delta == end &&
                call.Radius >= body.Radius && call.HalfHeight >= body.HalfHeight);
            start = end;
        }
        Assert.Equal(0, scene.View.LegacySweepCalls);
    }

    delegate MovementAvailability ResolveDelegate(in MovementBodyQuery body, Vector3 displacement,
        MovementQueryLease queries, Span<Vector3> destination, out int written, out bool blocked);

    static ResolveDelegate Resolver()
    {
        Type? type = typeof(MovementQueryLease).Assembly.GetType("KhaozEngine.Locomotion.MovementCapsuleResolver");
        Assert.NotNull(type);
        MethodInfo? method = type.GetMethod("TryResolveSolids", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.CreateDelegate<ResolveDelegate>();
    }

    sealed record Capture(MovementAvailability Status, Vector3[] Path, int Count, bool Blocked)
    {
        public Vector3 End => Path[Count - 1];
    }

    sealed class Scene : IDisposable
    {
        public readonly BepuPhysicsWorld World = new(Vector3.Zero);
        public StaticHandle[] Excluded = [];
        public ResolverQueryView View = null!;
        public EnvironmentAcquisitionFixture Environment = null!;
        public bool Cold;
        public bool Stale;
        public bool FailAfterProgress;
        public bool StaleDuringSweep;
        public bool WideBracket;
        public bool OmitImpactContacts;
        public bool EndlessConstraints;
        public StaticHandle WallX(float x = 2) => World.AddStatic(new BoxShape(new(0.03125f, 3, 8)), Pose.At(new(x, 0, 0)));
        public void WallZ() => World.AddStatic(new BoxShape(new(8, 3, 0.03125f)), Pose.At(new(0, 0, 2)));
        public void Floor() => World.AddStatic(new BoxShape(new(8, 0.125f, 8)), Pose.At(new(0, -0.125f, 0)));

        public Capture Resolve(MovementBodyQuery body, Vector3 delta, int capacity = 9)
        {
            using var selected = World.CreateQueryViewExcludingStatics(Excluded);
            using var view = new ResolverQueryView(selected)
            {
                FailAfterProgress = FailAfterProgress,
                WideBracket = WideBracket,
                OmitImpactContacts = OmitImpactContacts,
                EndlessConstraints = EndlessConstraints
            };
            View = view;
            var identity = new MovementQueryIdentity("closure", 1, "scope");
            var frame = new MovementFrameDescriptor(WorldFrame.Origin, World.Origin, 1);
            var scope = new MovementQueryScope(new(-8), new(8), 0, 0, "world", Cold ? null : Room, identity, frame);
            Environment = new EnvironmentAcquisitionFixture(view, scope);
            var acquired = Environment.Acquire();
            Assert.Equal(MovementAvailability.Known, acquired.Status);
            using var lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
            Assert.Throws<InvalidOperationException>(() => World.Step(1f / 30f));
            if (Stale) Environment.Fault = "stale-pin";
            if (StaleDuringSweep) view.AfterSweep = () => Environment.Fault = "stale-pin";
            var destination = new Vector3[capacity];
            Array.Fill(destination, Sentinel);
            MovementAvailability status = Resolver()(body, delta, lease, destination, out int count, out bool blocked);
            return new Capture(status, destination, count, blocked);
        }
        public void Dispose() => World.Dispose();
    }

    readonly record struct Trace(Vector3 Centre, Vector3 Delta, float Radius, float HalfHeight, CapsuleSweepStatus Status);

    sealed class ResolverQueryView : SweepQueryView, IPhysicsCapsuleSweep, IPhysicsCapsuleContacts
    {
        readonly IPhysicsCapsuleSweep _sweep;
        readonly IPhysicsCapsuleContacts _contacts;
        public ResolverQueryView(IPhysicsWorldQueryView inner) : base(inner)
        {
            _sweep = (IPhysicsCapsuleSweep)inner;
            _contacts = (IPhysicsCapsuleContacts)inner;
        }
        public readonly System.Collections.Generic.List<Trace> Calls = [];
        public bool FailAfterProgress;
        public bool WideBracket;
        public bool OmitImpactContacts;
        public bool EndlessConstraints;
        public int NonzeroSweeps;
        Vector3 lastDelta;
        public readonly System.Collections.Generic.List<Vector3[]> ContactSets = [];
        bool progressed;
        public Action? AfterSweep;
        public CapsuleSweepResult SweepCapsuleCertified(CapsuleShape capsule, Pose pose, Vector3 delta,
            QueryFilter filter = default)
        {
            if (delta != Vector3.Zero) { NonzeroSweeps++; lastDelta = delta; }
            CapsuleSweepResult result = delta != Vector3.Zero && WideBracket
                ? new(CapsuleSweepStatus.Hit, 1, 3, 0.001f)
                : delta != Vector3.Zero && EndlessConstraints ? new(CapsuleSweepStatus.Hit, 0, 0, 0)
                : FailAfterProgress && progressed ? default :
                _sweep.SweepCapsuleCertified(capsule, pose, delta, filter);
            Calls.Add(new(pose.Position, delta, capsule.Radius, capsule.Length * 0.5f + capsule.Radius, result.Status));
            if (result.Status == CapsuleSweepStatus.Clear && delta != Vector3.Zero) progressed = true;
            AfterSweep?.Invoke();
            return result;
        }

        // Adversarial consumer modes are synthetic result injection, not backend geometry evidence.
        public new CapsuleContactResult QueryCapsuleContacts(CapsuleShape capsule, Pose pose, float margin,
            Span<CapsuleContact> destination, QueryFilter filter = default)
        {
            if (NonzeroSweeps > 0 && OmitImpactContacts) return new(true, 0, 0, 0);
            if (NonzeroSweeps > 0 && EndlessConstraints)
            {
                Vector3 normal = Vector3.Normalize(new Vector3(-lastDelta.X + lastDelta.Z, 0, -lastDelta.Z - lastDelta.X));
                destination[0] = new(normal, 0, false, 1, 0, 0);
                return new(true, 1, 1, 0);
            }
            CapsuleContactResult result = _contacts.QueryCapsuleContacts(capsule, pose, margin, destination, filter);
            var normals = new Vector3[result.Written];
            for (int i = 0; i < normals.Length; i++) normals[i] = destination[i].Normal;
            ContactSets.Add(normals);
            return result;
        }
    }
}
