using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using BepuStaticHandle = global::BepuPhysics.StaticHandle;
using SeamStaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Tests.Physics;

public class PhysicsQueryViewLifecycleTests
{
    static readonly BoxShape UnitBox = new(new Vector3(0.5f));
    static readonly CapsuleShape Capsule = new(0.3f, 0.9f);

    [Fact]
    public void InputSnapshotSurvivesArrayMutation()
    {
        using IPhysicsWorld owner = new BepuPhysicsWorld(Vector3.Zero);
        SeamStaticHandle near = owner.AddStatic(UnitBox, Pose.At(new Vector3(2f, 0f, 0f)));
        SeamStaticHandle far = owner.AddStatic(UnitBox, Pose.At(new Vector3(5f, 0f, 0f)));
        SeamStaticHandle[] requested = { near, near };
        using IPhysicsWorldQueryView view = owner.CreateQueryViewExcludingStatics(requested);
        requested[0] = far;
        requested[1] = far;

        Assert.True(view.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit ray));
        Assert.Equal(far, ray.Body);
        Assert.True(view.SweepCapsule(Capsule, Pose.Identity, Vector3.UnitX, 8f, out SweepHit sweep));
        Assert.Equal(far, sweep.Body);
        Assert.False(view.ComputePenetration(Capsule, Pose.At(new Vector3(2f, 0f, 0f)), out _));
        Assert.True(view.ComputePenetration(Capsule, Pose.At(new Vector3(5f, 0f, 0f)), out _));
        Assert.True(owner.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit complete));
        Assert.Equal(near, complete.Body);
    }

    [Fact]
    public void RemovedExcludedHandleDoesNotExcludeReplacement()
    {
        using IPhysicsWorld owner = new BepuPhysicsWorld(Vector3.Zero);
        SeamStaticHandle excluded = owner.AddStatic(UnitBox, Pose.At(new Vector3(2f, 0f, 0f)));
        BepuStaticHandle backendHandle = BackendHandle(owner, excluded);
        using IPhysicsWorldQueryView view = owner.CreateQueryViewExcludingStatics(new[] { excluded });
        owner.RemoveStatic(excluded);
        SeamStaticHandle replacement = owner.AddStatic(UnitBox, Pose.At(new Vector3(2f, 0f, 0f)));

        Assert.NotEqual(excluded, replacement);
        Assert.Equal(backendHandle, BackendHandle(owner, replacement));
        Assert.True(view.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit ray));
        Assert.Equal(replacement, ray.Body);
        Assert.True(view.SweepCapsule(Capsule, Pose.Identity, Vector3.UnitX, 8f, out SweepHit sweep));
        Assert.Equal(replacement, sweep.Body);
        Assert.True(view.ComputePenetration(Capsule, Pose.At(new Vector3(2f, 0f, 0f)), out _));
    }

    [Fact]
    public void EmptySelectionReturnsViewWithOwnerQueryParity()
    {
        using IPhysicsWorld owner = new BepuPhysicsWorld(Vector3.Zero);
        owner.AddStatic(UnitBox, Pose.At(new Vector3(2f, 0f, 0f)));
        using IPhysicsWorldQueryView view = owner.CreateQueryViewExcludingStatics(Array.Empty<SeamStaticHandle>());

        Assert.NotSame(owner, view);
        Assert.Same(owner, view.SourceWorld);
        Assert.False(view.CanRebase);
        Assert.Equal(owner.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit expectedRay),
            view.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit actualRay));
        Assert.Equal(expectedRay, actualRay);
        Assert.Equal(owner.SweepCapsule(Capsule, Pose.Identity, Vector3.UnitX, 8f, out SweepHit expectedSweep),
            view.SweepCapsule(Capsule, Pose.Identity, Vector3.UnitX, 8f, out SweepHit actualSweep));
        Assert.Equal(expectedSweep, actualSweep);
        Pose overlap = Pose.At(new Vector3(2f, 0f, 0f));
        Assert.Equal(owner.ComputePenetration(Capsule, overlap, out Vector3 expectedMtv),
            view.ComputePenetration(Capsule, overlap, out Vector3 actualMtv));
        Assert.Equal(expectedMtv, actualMtv);
    }

    [Fact]
    public void MissingOrStaleStaticIsRejected()
    {
        using IPhysicsWorld owner = new BepuPhysicsWorld(Vector3.Zero);
        SeamStaticHandle live = owner.AddStatic(UnitBox, Pose.At(new Vector3(2f, 0f, 0f)));
        ArgumentException missing = Assert.Throws<ArgumentException>(() =>
            owner.CreateQueryViewExcludingStatics(new[] { live, new SeamStaticHandle(int.MaxValue) }));
        Assert.Equal("excludedStatics", missing.ParamName);
        Assert.True(owner.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit hit));
        Assert.Equal(live, hit.Body);

        owner.RemoveStatic(live);
        ArgumentException stale = Assert.Throws<ArgumentException>(() =>
            owner.CreateQueryViewExcludingStatics(new[] { live }));
        Assert.Equal("excludedStatics", stale.ParamName);
        Assert.False(owner.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out _));
    }

    [Fact]
    public void DefaultFactoryIsExplicitlyUnsupported()
    {
        using IPhysicsWorld owner = new UnsupportedWorld();
        Assert.Throws<NotSupportedException>(() =>
            owner.CreateQueryViewExcludingStatics(Array.Empty<SeamStaticHandle>()));
        Assert.Throws<NotSupportedException>(() =>
            owner.CreateQueryViewExcludingStatics(new[] { new SeamStaticHandle(0) }));
    }

    [Fact]
    public void ViewMutationsCannotTouchOwner()
    {
        using IPhysicsWorld owner = new BepuPhysicsWorld(Vector3.Zero);
        using IPhysicsWorld reference = new BepuPhysicsWorld(Vector3.Zero);
        var scene = AddMutationScene(owner);
        var control = AddMutationScene(reference);
        using IPhysicsWorldQueryView view = owner.CreateQueryViewExcludingStatics(Array.Empty<SeamStaticHandle>());
        Assert.Same(owner, view.SourceWorld);
        Assert.False(view.CanRebase);
        AssertDynamicReads(owner, view, scene.Moving);
        AssertDynamicReads(owner, view, scene.JointBody);
        OwnerState before = CaptureOwner(owner, scene.Moving, scene.JointBody);

        foreach (Action mutation in DeniedMutations(view, scene.Solid, scene.Moving, scene.Joint, scene.Description))
        {
            Assert.Throws<NotSupportedException>(mutation);
            Assert.Equal(before, CaptureOwner(owner, scene.Moving, scene.JointBody));
        }

        owner.Step(1f / 60f);
        reference.Step(1f / 60f);
        Assert.Equal(CaptureOwner(reference, control.Moving, control.JointBody),
            CaptureOwner(owner, scene.Moving, scene.JointBody));
        AssertDynamicReads(owner, view, scene.Moving);
        AssertDynamicReads(owner, view, scene.JointBody);
    }

    [Fact]
    public void ViewDisposalLeavesOwnerUsable()
    {
        using IPhysicsWorld owner = new BepuPhysicsWorld(Vector3.Zero);
        using IPhysicsWorld reference = new BepuPhysicsWorld(Vector3.Zero);
        var scene = AddMutationScene(owner);
        var control = AddMutationScene(reference);
        IPhysicsWorldQueryView view = owner.CreateQueryViewExcludingStatics(new[] { scene.Solid });
        OwnerState before = CaptureOwner(owner, scene.Moving, scene.JointBody);

        view.Dispose();
        view.Dispose();
        Assert.Same(owner, view.SourceWorld);
        Assert.False(view.CanRebase);
        Assert.Equal(before, CaptureOwner(owner, scene.Moving, scene.JointBody));
        owner.Step(1f / 60f);
        reference.Step(1f / 60f);
        Assert.Equal(CaptureOwner(reference, control.Moving, control.JointBody),
            CaptureOwner(owner, scene.Moving, scene.JointBody));
        Assert.True(owner.GetDynamicPose(scene.Moving).Position.X > 20f);
        owner.SetConstraintTarget(scene.Joint, 0.2f);
        owner.RemoveConstraint(scene.Joint);
        owner.RemoveDynamic(scene.JointBody);
        owner.RemoveStatic(scene.Solid);
        Assert.False(owner.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out _));
        SeamStaticHandle replacement = owner.AddStatic(UnitBox, Pose.At(new Vector3(2f, 0f, 0f)));
        Assert.True(owner.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit hit));
        Assert.Equal(replacement, hit.Body);
    }

    [Fact]
    public void DisposedViewRejectsOperationalCalls()
    {
        using IPhysicsWorld owner = new BepuPhysicsWorld(Vector3.Zero);
        var scene = AddMutationScene(owner);
        IPhysicsWorldQueryView view = owner.CreateQueryViewExcludingStatics(Array.Empty<SeamStaticHandle>());
        OwnerState before = CaptureOwner(owner, scene.Moving, scene.JointBody);
        view.Dispose();

        foreach (Action mutation in DeniedMutations(view, scene.Solid, scene.Moving, scene.Joint, scene.Description))
            Assert.Throws<ObjectDisposedException>(mutation);
        Assert.Throws<ObjectDisposedException>(() => view.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out _));
        Assert.Throws<ObjectDisposedException>(() => view.SweepCapsule(Capsule, Pose.Identity, Vector3.UnitX, 8f, out _));
        Assert.Throws<ObjectDisposedException>(() => view.ComputePenetration(Capsule, Pose.Identity, out _));
        Assert.Throws<ObjectDisposedException>(() => view.GetDynamicPose(scene.Moving));
        Assert.Throws<ObjectDisposedException>(() => view.GetDynamicVelocity(scene.Moving, out _, out _));
        Assert.Throws<ObjectDisposedException>(() => view.IsAwake(scene.Moving));
        Assert.Throws<ObjectDisposedException>(() => { _ = view.Origin; });
        Assert.Same(owner, view.SourceWorld);
        Assert.False(view.CanRebase);
        Assert.Equal(before, CaptureOwner(owner, scene.Moving, scene.JointBody));
        view.Dispose();
    }

    [Fact]
    public void ViewReadsOwnerOriginAfterRebase()
    {
        using IPhysicsWorld owner = new BepuPhysicsWorld(Vector3.Zero);
        Vector3 firstOrigin = new(128f, 0f, -128f);
        owner.Rebase(firstOrigin);
        SeamStaticHandle near = owner.AddStatic(UnitBox, Pose.At(new Vector3(2f, 0f, 0f)));
        SeamStaticHandle far = owner.AddStatic(UnitBox, Pose.At(new Vector3(5f, 0f, 0f)));
        DynamicBodyHandle body = owner.AddDynamic(UnitBox, Pose.At(new Vector3(20f, 0f, 0f)),
            DynamicBodyDescription.WithMass(1f));
        using IPhysicsWorldQueryView view = owner.CreateQueryViewExcludingStatics(new[] { near });
        Assert.Equal(firstOrigin, view.Origin);
        Assert.True(view.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit beforeRay));
        Assert.Equal(far, beforeRay.Body);
        Assert.True(view.SweepCapsule(Capsule, Pose.Identity, Vector3.UnitX, 8f, out SweepHit beforeSweep));
        Vector3 nextOrigin = new(256f, 32f, -256f);
        owner.Rebase(nextOrigin);
        Vector3 delta = firstOrigin - nextOrigin;

        Assert.Equal(nextOrigin, view.Origin);
        Assert.Same(owner, view.SourceWorld);
        Assert.False(view.CanRebase);
        Assert.True(view.Raycast(delta, Vector3.UnitX, 8f, out RayHit afterRay));
        Assert.Equal(far, afterRay.Body);
        Assert.Equal(beforeRay.Distance, afterRay.Distance);
        Assert.Equal(beforeRay.Point + delta, afterRay.Point);
        Assert.True(view.SweepCapsule(Capsule, Pose.At(delta), Vector3.UnitX, 8f, out SweepHit afterSweep));
        Assert.Equal(far, afterSweep.Body);
        Assert.InRange(MathF.Abs(beforeSweep.Distance - afterSweep.Distance), 0f, 0.00003f);
        Assert.False(view.ComputePenetration(Capsule, Pose.At(new Vector3(2f, 0f, 0f) + delta), out _));
        Assert.True(view.ComputePenetration(Capsule, Pose.At(new Vector3(5f, 0f, 0f) + delta), out _));
        AssertDynamicReads(owner, view, body);
        Assert.True(owner.Raycast(delta, Vector3.UnitX, 8f, out RayHit complete));
        Assert.Equal(near, complete.Body);
    }

    static Action[] DeniedMutations(IPhysicsWorld view, SeamStaticHandle solid, DynamicBodyHandle body,
        ConstraintHandle joint, ConstraintDescription description) => new Action[]
    {
        () => view.AddStatic(UnitBox, Pose.Identity),
        () => view.RemoveStatic(solid),
        () => view.AddDynamic(UnitBox, Pose.Identity, DynamicBodyDescription.WithMass(1f)),
        () => view.RemoveDynamic(body),
        () => view.SetDynamicVelocity(body, new Vector3(3f, 4f, 5f), Vector3.UnitY),
        () => view.AddConstraint(in description),
        () => view.RemoveConstraint(joint),
        () => view.SetConstraintTarget(joint, 1f),
        () => view.Step(1f / 60f),
        () => view.Rebase(new Vector3(128f, 0f, 0f)),
        () => view.CreateQueryViewExcludingStatics(Array.Empty<SeamStaticHandle>()),
    };

    static (SeamStaticHandle Solid, DynamicBodyHandle Moving, DynamicBodyHandle JointBody,
        ConstraintHandle Joint, ConstraintDescription Description) AddMutationScene(IPhysicsWorld owner)
    {
        SeamStaticHandle solid = owner.AddStatic(UnitBox, Pose.At(new Vector3(2f, 0f, 0f)));
        DynamicBodyHandle moving = owner.AddDynamic(UnitBox, Pose.At(new Vector3(20f, 0f, 0f)),
            new DynamicBodyDescription(1f) { LinearVelocity = Vector3.UnitX, SleepThreshold = 0f });
        Vector3 pivot = new(20f, 5f, 0f);
        DynamicBodyHandle jointBody = owner.AddDynamic(UnitBox, Pose.At(pivot),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });
        ConstraintDescription description = ConstraintDescription.HingeJoint(
            ConstraintAttachment.OnBody(jointBody), ConstraintAttachment.AtWorld(pivot),
            Vector3.Zero, Vector3.Zero, Vector3.UnitZ, Vector3.UnitZ).WithHingeServo(0f);
        ConstraintHandle joint = owner.AddConstraint(description);
        return (solid, moving, jointBody, joint, description);
    }

    static void AssertDynamicReads(IPhysicsWorld owner, IPhysicsWorldQueryView view, DynamicBodyHandle body)
    {
        Assert.Equal(owner.GetDynamicPose(body), view.GetDynamicPose(body));
        owner.GetDynamicVelocity(body, out Vector3 ownerLinear, out Vector3 ownerAngular);
        view.GetDynamicVelocity(body, out Vector3 viewLinear, out Vector3 viewAngular);
        Assert.Equal(ownerLinear, viewLinear);
        Assert.Equal(ownerAngular, viewAngular);
        Assert.Equal(owner.IsAwake(body), view.IsAwake(body));
    }

    static OwnerState CaptureOwner(IPhysicsWorld owner, DynamicBodyHandle moving, DynamicBodyHandle jointBody)
    {
        Assert.True(owner.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit ray));
        owner.GetDynamicVelocity(moving, out Vector3 linear, out Vector3 angular);
        return new OwnerState(ray, owner.GetDynamicPose(moving), owner.GetDynamicPose(jointBody), linear, angular,
            owner.IsAwake(moving), owner.Origin, MapCount(owner, "_handles"), MapCount(owner, "_dynamics"),
            MapCount(owner, "_constraints"));
    }

    static int MapCount(IPhysicsWorld owner, string name) =>
        Assert.IsAssignableFrom<IDictionary>(BackendField(owner, name)).Count;

    static BepuStaticHandle BackendHandle(IPhysicsWorld owner, SeamStaticHandle seam)
    {
        var reverse = Assert.IsType<Dictionary<int, int>>(BackendField(owner, "_reverseHandles"));
        return new BepuStaticHandle(Assert.Single(reverse, pair => pair.Value == seam.Value).Key);
    }

    static object BackendField(IPhysicsWorld owner, string name)
    {
        FieldInfo field = typeof(BepuPhysicsWorld).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.NotNull(field);
        object? value = field.GetValue(owner);
        Assert.NotNull(value);
        return value;
    }

    readonly record struct OwnerState(RayHit Ray, Pose MovingPose, Pose JointPose, Vector3 Linear, Vector3 Angular,
        bool Awake, Vector3 Origin, int StaticCount, int DynamicCount, int ConstraintCount);

    sealed class UnsupportedWorld : IPhysicsWorld
    {
        public SeamStaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) => default;
        public void RemoveStatic(SeamStaticHandle handle) { }
        public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body, PhysicsMaterial? material = null) => default;
        public void RemoveDynamic(DynamicBodyHandle handle) { }
        public Pose GetDynamicPose(DynamicBodyHandle handle) => Pose.Identity;
        public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular) { linear = default; angular = default; }
        public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular) { }
        public bool IsAwake(DynamicBodyHandle handle) => false;
        public ConstraintHandle AddConstraint(in ConstraintDescription description) => default;
        public void RemoveConstraint(ConstraintHandle handle) { }
        public void SetConstraintTarget(ConstraintHandle handle, float target) { }
        public void Step(float dt) { }
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter = default) { hit = default; return false; }
        public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance, out SweepHit hit, QueryFilter filter = default) { hit = default; return false; }
        public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv) { mtv = default; return false; }
        public void Dispose() { }
    }
}
