using System;
using System.Numerics;
using System.Threading;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

// Scripted complete values test the real owner's consumption protocol, never a geometry certificate.
public class CapsuleFeatureLifetimeTests
{
    static readonly CapsuleShape Capsule = new(0.3f, 0.9f);

    [Fact]
    public void OriginalOwnerAndViewAcceptOnlyTheirOwnLiveResult()
    {
        using var scene = new Scene();
        CapsuleFeatureResult owner = scene.Result(scene.World);
        Capability(scene.World).AssertFeatureCurrent(owner, scene.Lease);
        CapsuleFeatureResult view = scene.Result(scene.View);
        Capability(scene.View).AssertFeatureCurrent(view, scene.Lease);
    }

    [Fact]
    public void MatchingSourceMetadataDoesNotAuthorizeAnotherReceiver()
    {
        using var scene = new Scene();
        IPhysicsCapsuleFeatures owner = Capability(scene.World), other = Capability(scene.Excluded);
        CapsuleFeatureResult result = scene.Result(scene.View);
        Assert.Throws<InvalidOperationException>(() => owner.AssertFeatureCurrent(result, scene.Lease));
        Assert.Throws<InvalidOperationException>(() => other.AssertFeatureCurrent(result, scene.Lease));
    }

    [Fact]
    public void AnAuthenticLeaseFromAnotherOwnerIsRefusedWithoutTouchingOutput()
    {
        using var scene = new Scene();
        using var other = new BepuPhysicsWorld(Vector3.Zero);
        using IPhysicsQueryLease foreign = other.AcquireQueryReadLease();
        IPhysicsCapsuleFeatures capability = Capability(scene.View);
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        Assert.ThrowsAny<ArgumentException>(() => capability.QueryCapsuleFeature(foreign, scene.Target,
            Capsule, Pose.Identity, 0.001f, faces));
        Assert.Equal(original, faces);
    }

    [Fact]
    public void AUserLeaseWithMatchingMetadataCannotAuthenticateTheOwner()
    {
        using var scene = new Scene();
        IPhysicsCapsuleFeatures capability = Capability(scene.View);
        var forged = new ForgedLease(scene.Lease);
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        Assert.ThrowsAny<ArgumentException>(() => capability.QueryCapsuleFeature(forged, scene.Target,
            Capsule, Pose.Identity, 0.001f, faces));
        Assert.Equal(0, forged.CurrentCalls);
        Assert.Equal(original, faces);
    }

    [Fact]
    public void ANewSameGenerationLeaseCannotReviveAnOldResult()
    {
        using var scene = new Scene();
        IPhysicsCapsuleFeatures capability = Capability(scene.View);
        CapsuleFeatureResult result = scene.Result(scene.View);
        scene.Lease.Dispose();
        using IPhysicsQueryLease next = ((IPhysicsQueryLeaseSource)scene.View).AcquireQueryReadLease();
        Assert.Equal(result.GeometryGeneration, next.GeometryGeneration);
        Assert.Equal(result.Origin, next.Origin);
        Assert.Same(result.SourceWorld, next.SourceWorld);
        Assert.Throws<InvalidOperationException>(() => capability.AssertFeatureCurrent(result, next));
    }

    [Fact]
    public void ExpiredLeaseRefusesConsumptionAndQueryWithoutWritingFaces()
    {
        using var scene = new Scene();
        IPhysicsCapsuleFeatures capability = Capability(scene.View);
        CapsuleFeatureResult result = scene.Result(scene.View);
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        scene.Lease.Dispose();
        Assert.ThrowsAny<InvalidOperationException>(() => capability.AssertFeatureCurrent(result, scene.Lease));
        Assert.ThrowsAny<InvalidOperationException>(() => capability.QueryCapsuleFeature(scene.Lease,
            scene.Target, Capsule, Pose.Identity, 0.001f, faces));
        Assert.Equal(original, faces);
    }

    [Fact]
    public void DisposedReceiverRefusesEvenWhileTheOwnerHasAValidLease()
    {
        using var scene = new Scene();
        IPhysicsCapsuleFeatures capability = Capability(scene.View);
        scene.Lease.Dispose();
        scene.View.Dispose();
        using IPhysicsQueryLease next = scene.World.AcquireQueryReadLease();
        CapsuleFeatureResult scripted = Scene.Result(scene.View, next, scene.Target);
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        Assert.Throws<ObjectDisposedException>(() => capability.AssertFeatureCurrent(scripted, next));
        Assert.Throws<ObjectDisposedException>(() => capability.QueryCapsuleFeature(next, scene.Target,
            Capsule, Pose.Identity, 0.001f, faces));
        Assert.Equal(original, faces);
    }

    [Fact]
    public void WrongThreadIsRejectedBeforeWaitingOnItsOwnHeldLease()
    {
        using var scene = new Scene();
        IPhysicsCapsuleFeatures capability = Capability(scene.View);
        CapsuleFeatureResult result = scene.Result(scene.View);
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        Exception? queryError = null, consumeError = null;
        var worker = new Thread(() =>
        {
            queryError = Record.Exception(() => capability.QueryCapsuleFeature(scene.Lease, scene.Target,
                Capsule, Pose.Identity, 0.001f, faces));
            consumeError = Record.Exception(() => capability.AssertFeatureCurrent(result, scene.Lease));
        })
        { IsBackground = true };
        bool completedWhileHeld;
        worker.Start();
        try { completedWhileHeld = worker.Join(TimeSpan.FromSeconds(5)); }
        finally { scene.Lease.Dispose(); }
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)), "The worker must finish after cleanup.");
        Assert.True(completedWhileHeld, "Authentication must precede owner monitor entry.");
        Assert.IsType<InvalidOperationException>(queryError);
        Assert.IsType<InvalidOperationException>(consumeError);
        Assert.Equal(original, faces);
    }

    [Fact]
    public void ExcludedAndMissingTargetsReturnNoUsablePrefix()
    {
        using var scene = new Scene();
        IPhysicsCapsuleFeatures excluded = Capability(scene.Excluded), visible = Capability(scene.View);
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        CapsuleFeatureResult hidden = excluded.QueryCapsuleFeature(scene.Lease, scene.Target,
            Capsule, Pose.Identity, 0.001f, faces);
        CapsuleFeatureResult missing = visible.QueryCapsuleFeature(scene.Lease, new StaticHandle(int.MaxValue),
            Capsule, Pose.Identity, 0.001f, faces);
        Assert.Equal(CapsuleFeatureStatus.Unavailable, hidden.Status);
        Assert.Equal(CapsuleFeatureStatus.Unavailable, missing.Status);
        Assert.Equal(0, hidden.Written);
        Assert.Equal(0, missing.Written);
        Assert.Equal(original, faces);
    }

    [Fact]
    public void ArgumentFailuresLeaveTheWholeDestinationUntouched()
    {
        using var scene = new Scene();
        IPhysicsCapsuleFeatures capability = Capability(scene.View);
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        Assert.ThrowsAny<ArgumentException>(() => capability.QueryCapsuleFeature(null!, scene.Target,
            Capsule, Pose.Identity, 0.001f, faces));
        Assert.ThrowsAny<ArgumentException>(() => capability.QueryCapsuleFeature(scene.Lease, scene.Target,
            null!, Pose.Identity, 0.001f, faces));
        Assert.ThrowsAny<ArgumentException>(() => capability.QueryCapsuleFeature(scene.Lease, scene.Target,
            Capsule, Pose.At(new Vector3(float.NaN, 0, 0)), 0.001f, faces));
        Assert.ThrowsAny<ArgumentException>(() => capability.QueryCapsuleFeature(scene.Lease, scene.Target,
            Capsule, Pose.Identity, -1, faces));
        Assert.ThrowsAny<ArgumentException>(() => capability.QueryCapsuleFeature(scene.Lease, scene.Target,
            new CapsuleShape(0, 1), Pose.Identity, 0.001f, faces));
        Assert.ThrowsAny<ArgumentException>(() => capability.QueryCapsuleFeature(scene.Lease, scene.Target,
            Capsule, Pose.Identity, 0.001f, faces, new QueryFilter((QueryMobility)255)));
        Assert.Equal(original, faces);
        scene.Lease.AssertCurrent();
    }

    [Fact]
    public void AUnitTiltOutsideTheUprightDomainRefusesWithoutWritingFaces()
    {
        using var scene = new Scene();
        IPhysicsCapsuleFeatures capability = Capability(scene.View);
        CapsuleFeatureResult upright = capability.QueryCapsuleFeature(scene.Lease, scene.Target,
            Capsule, Pose.Identity, 0.001f, new CapsuleIncidentFace[2]);
        Assert.NotEqual(CapsuleFeatureStatus.Unsupported, upright.Status);
        Quaternion rotation = new(0.5f, 0.5f, 0.5f, 0.5f);
        Assert.Equal(1f, rotation.LengthSquared());
        CapsuleIncidentFace[] faces = Sentinels(), original = (CapsuleIncidentFace[])faces.Clone();
        CapsuleFeatureResult tilted = capability.QueryCapsuleFeature(scene.Lease, scene.Target,
            Capsule, new Pose(Vector3.Zero, rotation), 0.001f, faces);
        Assert.Equal(CapsuleFeatureStatus.Unsupported, tilted.Status);
        Assert.Equal(0, tilted.Written);
        Assert.Null(tilted.Lease);
        Assert.Equal(original, faces);
    }

    [Fact]
    public void ARefusalCannotBeConsumedAsACurrentFeature()
    {
        using var scene = new Scene();
        IPhysicsCapsuleFeatures capability = Capability(scene.View);
        Assert.Throws<InvalidOperationException>(() => capability.AssertFeatureCurrent(default, scene.Lease));
    }

    static IPhysicsCapsuleFeatures Capability(IPhysicsWorld world) => Assert.IsAssignableFrom<IPhysicsCapsuleFeatures>(world);
    static CapsuleIncidentFace[] Sentinels() =>
        [new(999, Vector3.UnitX, 0, CapsuleFeatureKind.OpenBoundary), new(998, Vector3.UnitZ, 0, CapsuleFeatureKind.Vertex)];

    sealed class ForgedLease(IPhysicsQueryLease original) : IPhysicsQueryLease
    {
        public IPhysicsWorld SourceWorld => original.SourceWorld;
        public Vector3 Origin => original.Origin;
        public long GeometryGeneration => original.GeometryGeneration;
        public int CurrentCalls { get; private set; }
        public void AssertCurrent() => CurrentCalls++;
        public void Dispose() { }
    }

    sealed class Scene : IDisposable
    {
        internal readonly BepuPhysicsWorld World = new(Vector3.Zero);
        internal readonly IPhysicsWorldQueryView View, Excluded;
        internal readonly IPhysicsQueryLease Lease;
        internal readonly StaticHandle Target;
        internal Scene()
        {
            Target = World.AddStatic(new BoxShape(new Vector3(0.5f)), Pose.Identity);
            View = World.CreateQueryViewExcludingStatics([]);
            Excluded = World.CreateQueryViewExcludingStatics([Target]);
            Lease = ((IPhysicsQueryLeaseSource)View).AcquireQueryReadLease();
            Lease.AssertCurrent();
            Assert.True(View.Raycast(new Vector3(0, 2, 0), -Vector3.UnitY, 4, out RayHit hit));
            Assert.Equal(Target, hit.Body);
            Assert.False(Excluded.Raycast(new Vector3(0, 2, 0), -Vector3.UnitY, 4, out _));
        }
        internal CapsuleFeatureResult Result(IPhysicsWorld receiver) => Result(receiver, Lease, Target);
        internal static CapsuleFeatureResult Result(IPhysicsWorld receiver, IPhysicsQueryLease lease, StaticHandle target) =>
            CapsuleFeatureResult.Completed(receiver, lease, target, 0, 0, CapsuleFeatureKind.FaceInterior,
                Vector3.UnitY, Vector3.Zero, Vector3.UnitY, 0, 0, 0, 0,
                new[] { new CapsuleIncidentFace(0, Vector3.UnitY, 0, CapsuleFeatureKind.FaceInterior) });
        public void Dispose()
        {
            Lease.Dispose();
            View.Dispose();
            Excluded.Dispose();
            World.Dispose();
        }
    }
}
