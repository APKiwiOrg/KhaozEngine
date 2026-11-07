using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainRuntimeFaultTests
{
    [Fact]
    public void MissingRetainedDependencyBlocksAndEndsOnlyThisAttempt()
    {
        using var scene = new ContactTerrainScene(4);
        var nav = ContactTerrainRuntimeTests.Load(scene);
        var path = nav.Planner.FindPath(scene.From, scene.To, nav.AgentRadius, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Complete, path.Status);
        var follower = new PathFollower(nav.Planner, new PathFollowConfig(), nav.Space);
        var route = follower.Tick(scene.From, scene.To, nav.AgentRadius, ContactTerrainScene.Dt);
        Assert.Equal(PathFollowState.Dropping, route.State);
        var traversal = route.DropTraversal!.Value;
        var original = nav.Terrain!.Evidence;
        nav.RebindTerrainEvidence(ContactTerrainEvidenceSubset.Build(original, new(original), ContactTerrainScene.Tuning).RequireDependencies());
        var drop = new ContactDropFollower(nav, traversal);
        var body = new MoveState
        {
            Position = scene.From + Vector3.UnitY * ContactTerrainScene.Tuning.CapsuleHalfHeight,
            Grounded = true,
            SpeedScale = 1
        };
        var result = drop.Tick(body, ContactTerrainScene.Tuning, false, ContactTerrainScene.Dt, scene.Context,
            new PlayerPathInput(0.7f), out bool complete, out bool replan);
        Assert.Equal(RangeMoveStatus.Blocked, result.Status);
        Assert.Equal(ContactDropRuntimeRefusal.DependencyMissing, result.DropRefusal);
        Assert.Equal(Vector2.Zero, result.WorldDirection);
        Assert.False(complete || replan);
        Assert.False(drop.Preparing);
        Assert.Equal(result, drop.Tick(body, ContactTerrainScene.Tuning, false, ContactTerrainScene.Dt, scene.Context,
            new PlayerPathInput(0.8f), out complete, out replan));
        nav.RebindTerrainEvidence(original);
        Assert.Equal(RangeMoveStatus.Dropping, new ContactDropFollower(nav, traversal).Tick(body, ContactTerrainScene.Tuning,
            false, ContactTerrainScene.Dt, scene.Context, new PlayerPathInput(0.7f), out _, out _).Status);
    }

    [Theory]
    [InlineData("alignment")]
    [InlineData("prefix")]
    [InlineData("descent")]
    [InlineData("fringe")]
    public void ResetAtEachStageReleasesOwnershipWhileOrdinaryMovementContinues(string phase)
    {
        using var scene = new ContactTerrainScene(4);
        var nav = ContactTerrainRuntimeTests.Load(scene);
        var tuning = ContactTerrainScene.Tuning;
        var move = new MoveToRange(nav);
        var body = new MoveState
        {
            Position = scene.From + new Vector3(-0.08f, tuning.CapsuleHalfHeight, 0.03f),
            Grounded = true,
            SpeedScale = 0.25f
        };
        var below = ReachTarget.Point(scene.To + Vector3.UnitY * tuning.CapsuleHalfHeight);
        bool descended = false;
        for (int tick = 0; tick < 240; tick++)
        {
            var input = new PlayerPathInput(0.7f + tick * 0.1f);
            var steering = move.Tick(body, tuning, below, 0, false, ContactTerrainScene.Dt, scene.Context, input);
            Assert.Contains(steering.Status, new[] { RangeMoveStatus.Following, RangeMoveStatus.Dropping });
            var feet = body.Position - Vector3.UnitY * tuning.CapsuleHalfHeight;
            bool stage = phase switch
            {
                "alignment" => steering.Status == RangeMoveStatus.Following && !descended,
                "prefix" => steering.Status == RangeMoveStatus.Dropping && body.Grounded && !descended,
                "descent" => !body.Grounded,
                _ => descended && body.Grounded && !nav.Footprint.Accepts(feet),
            };
            if (stage)
            {
                move.Reset();
                var here = ReachTarget.Point(body.Position);
                var released = move.Tick(body, tuning, here, 0, false, ContactTerrainScene.Dt, scene.Context, input);
                Assert.NotEqual(RangeMoveStatus.Dropping, released.Status);
                var held = ContactTerrainRuntimeTests.Step(scene.Context, body, input.Command(new(Vector2.Zero, released.Status), false));
                if (!body.Grounded) Assert.True(held.Position.Y < body.Position.Y);
                return;
            }
            body = ContactTerrainRuntimeTests.Step(scene.Context, body, input.Command(steering, false));
            descended |= !body.Grounded;
        }
        Assert.Fail("The command path did not exercise the requested cancellation stage.");
    }
}
