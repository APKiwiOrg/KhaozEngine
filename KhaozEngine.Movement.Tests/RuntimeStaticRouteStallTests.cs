using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement;

public class RuntimeStaticRouteStallTests
{
    [Fact]
    public void AuthoredRouteStallsOnActualTravelAndResumesAfterRemovalAndReset()
    {
        const float dt = 1f / 30, travel = 0.1f;
        const int window = 12;
        MoveTuning tuning = Tuning with { WalkSpeed = 4, RunSpeed = 6 };
        using var scene = new OrdinaryContactScene(tuning,
            install: (world, builder) => Box(world, builder, "floor", new(-8, -0.2f, -8), new(8, 0, 8)));
        using var context = Context(scene);
        var options = new PhysicsNavBakeOptions(-1.5f, -1.5f, 6.5f, 1.5f, 0.5f, 3, 7,
            tuning.MaxSlopeRadians, 128, 512, MaxEdgeProbeSteps: 160);
        NavBakeProfile[] profiles = [new("npc", tuning, default)];
        var sources = ContactBakeFile.Sources();
        var expected = new NavBakeExpectation(options, sources, profiles);
        GroundNavigationBake bake = ContactBakeFile.Bake(context, options, profiles);
        GroundNavigation navigation = bake.GetProfile("npc");
        var graph = navigation.Graph;
        byte[] bytes = ContactBakeFile.Write(bake), fingerprint = bake.Fingerprint.ToArray();
        byte[] identity = NavBakeIdentity.Encode(expected, NavBakeIdentity.CurrentEngineVersion);
        var environmentIdentity = scene.Scope.Identity;
        var body = new MoveState { Position = new(-0.75f, 0.75f, 0.25f), Grounded = true };
        Vector3 startFeet = new(body.Position.X, 0, body.Position.Z), goalFeet = new(5.25f, 0, 0.25f);
        var target = ReachTarget.Point(goalFeet + Vector3.UnitY * tuning.CapsuleHalfHeight);
        var mover = new MoveToRange(navigation, null, new() { Stall = new(window, travel) });
        var owner = new MovementSupportKey("world", "route-wall");
        AssertRoute();
        scene.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Set(owner,
            new BoxShape(new(0.2f, 1, 3)), Pose.At(new(2, 1, 0)))]);
        AssertUnchanged();
        AssertRoute();

        var positions = new List<Vector3>();
        bool blocked = false;
        for (int tick = 0; tick < 60; tick++)
        {
            positions.Add(body.Position);
            RangeSteering steering = mover.Tick(body, tuning, target, 0.1f, false, dt, context);
            bool breached = tick >= window && HorizontalTravel(positions[tick], positions[tick - window]) < travel;
            Assert.Equal(breached ? RangeMoveStatus.Blocked : RangeMoveStatus.Following, steering.Status);
            if (breached)
            {
                Assert.True(tick > window);
                Assert.True(body.Position.X > startFeet.X + 1);
                Assert.InRange(body.Position.X, 1.4f, 1.8f - tuning.CapsuleRadius - ShellMotion.ContactSkin * 0.5f);
                using var known = scene.Acquire();
                blocked = true;
                break;
            }
            body = NpcGroundMovement.Step(body, steering, false, dt, tuning, context);
            Assert.True(body.Grounded);
        }
        Assert.True(blocked);
        Assert.Equal(RangeMoveStatus.Blocked, mover.Tick(body, tuning, target, 0.1f, false, dt, context).Status);
        scene.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Remove(owner)]);
        AssertUnchanged();
        AssertRoute();
        Assert.Equal(RangeMoveStatus.Blocked, mover.Tick(body, tuning, target, 0.1f, false, dt, context).Status);
        mover.Reset();
        bool arrived = false;
        for (int tick = 0; tick < 40; tick++)
        {
            RangeSteering steering = mover.Tick(body, tuning, target, 0.1f, false, dt, context);
            if (steering.Status == RangeMoveStatus.InRange) { arrived = true; break; }
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            body = NpcGroundMovement.Step(body, steering, false, dt, tuning, context);
        }
        Assert.True(arrived);
        Assert.True(body.Position.X > 2.2f + tuning.CapsuleRadius);
        using var stillKnown = scene.Acquire();

        void AssertRoute()
        {
            navigation.Validate(scene.Binding);
            Assert.True(navigation.AllowsSegment(startFeet, goalFeet));
            Assert.Equal(NavPathStatus.Complete,
                navigation.Planner.FindPath(startFeet, goalFeet, tuning.CapsuleRadius, PathQueryBudget.Default).Status);
        }

        void AssertUnchanged()
        {
            Assert.Same(navigation, bake.GetProfile("npc"));
            Assert.Same(graph, navigation.Graph);
            Assert.Equal(bytes, ContactBakeFile.Write(bake));
            Assert.Equal(fingerprint, bake.Fingerprint.ToArray());
            Assert.Equal(identity, NavBakeIdentity.Encode(expected, NavBakeIdentity.CurrentEngineVersion));
            Assert.Equal(environmentIdentity, scene.Binding.CreateOrdinaryScope(scene.Frame).Identity);
            navigation.Validate(scene.Binding);
        }
    }

    static double HorizontalTravel(Vector3 a, Vector3 b)
    {
        double dx = (double)a.X - b.X, dz = (double)a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }
}
