using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public sealed class TileEntityAimPointTests
{
    const long Body = 100;
    const long Target = 101;

    [Fact]
    public void A_local_attacker_nominates_from_the_newest_remote_footprint()
    {
        using var scenario = DelayedMovingTarget();
        TileMoveState attacker = Locked(new TileCoord(20, 20, 0), Target);
        scenario.Client.Prediction.Reset(attacker);
        scenario.Client.EntityAimPointResolver = Nominate;

        Assert.Equal(2.2455373f, scenario.Client.LocalPose.Yaw, 5);

        bool Nominate(long id, TileRect footprint, int plane, out Vector2 aim)
        {
            Assert.Equal(Target, id);
            Assert.Equal(new TileRect(25, 22, 3, 3), footprint);
            Assert.Equal(2, plane);
            aim = new Vector2(footprint.X, footprint.Z + footprint.Height - 1);
            return true;
        }
    }

    [Fact]
    public void A_remote_attacker_nominates_from_the_delayed_moving_target_footprint()
    {
        using var scenario = DelayedMovingTarget();
        Assert.True(scenario.Client.TryGetLatestRemoteFootprint(Target, out TileRect latest, out _));
        Assert.Equal(new TileRect(25, 22, 3, 3), latest);
        scenario.Client.EntityAimPointResolver = Nominate;

        Assert.True(scenario.Client.TryGetRemotePose(Body, out TilePose pose));
        Assert.Equal(2.3561945f, pose.Yaw, 5);

        bool Nominate(long id, TileRect footprint, int plane, out Vector2 aim)
        {
            Assert.Equal(Target, id);
            Assert.Equal(new TileRect(23, 22, 2, 2), footprint);
            Assert.Equal(2, plane);
            aim = new Vector2(footprint.X, footprint.Z + footprint.Height - 1);
            return true;
        }
    }

    [Fact]
    public void A_remote_attacking_the_local_player_nominates_from_newest_prediction()
    {
        using var scenario = new TilePresentationScenario();
        TileMoveState body = Locked(new TileCoord(20, 21, 0), scenario.Client.LocalNetId);
        scenario.Snapshot((Body, body));
        scenario.Client.AdvancePresentation(.5f);
        scenario.Client.Prediction.Reset(TileMoveState.At(new TileCoord(20, 20, 0), TileDirection.N));
        scenario.Client.Prediction.Predict(TileCommand.WalkTo(new TileCoord(22, 20, 0), TileMoveMode.Walk));
        scenario.Client.Prediction.AdvancePresentation(.037f);
        Assert.Equal(new TileCoord(21, 20, 0), scenario.Client.Prediction.PredictedState.Tile);
        scenario.Client.EntityAimPointResolver = Nominate;

        Assert.True(scenario.Client.TryGetRemotePose(Body, out TilePose pose));
        Assert.Equal(2.3561945f, pose.Yaw, 5);

        bool Nominate(long id, TileRect footprint, int plane, out Vector2 aim)
        {
            Assert.Equal(scenario.Client.LocalNetId, id);
            Assert.Equal(new TileRect(21, 20, 1, 1), footprint);
            Assert.Equal(0, plane);
            aim = new Vector2(footprint.X, footprint.Z + 2);
            return true;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Missing_declined_or_nonfinite_nominations_keep_the_footprint_centre(int mode)
    {
        using var scenario = new TilePresentationScenario();
        TileMoveState target = TileMoveState.At(new TileCoord(24, 20, 0), TileDirection.S);
        target.FootprintSize = 3;
        scenario.Snapshot((Target, target));
        scenario.Client.Prediction.Reset(Locked(new TileCoord(20, 20, 0), Target));
        if (mode != 0) scenario.Client.EntityAimPointResolver = Nominate;

        Assert.Equal(1.7681919f, scenario.Client.LocalPose.Yaw, 5);

        bool Nominate(long id, TileRect footprint, int plane, out Vector2 aim)
        {
            aim = mode switch
            {
                1 => new Vector2(1000, -1000),
                2 => new Vector2(float.NaN, 20),
                3 => new Vector2(24, float.NaN),
                4 => new Vector2(float.PositiveInfinity, 20),
                _ => new Vector2(24, float.NegativeInfinity),
            };
            return mode != 1;
        }
    }

    [Fact]
    public void Unknown_and_removed_entity_targets_keep_the_existing_facing()
    {
        using var scenario = new TilePresentationScenario();
        int nominations = 0;
        scenario.Client.EntityAimPointResolver = Nominate;
        scenario.Client.Prediction.Reset(Locked(new TileCoord(20, 20, 0), Target));
        Assert.Equal(MathF.PI, scenario.Client.LocalPose.Yaw, 5);
        Assert.Equal(0, nominations);

        scenario.Snapshot((Target, TileMoveState.At(new TileCoord(24, 20, 0), TileDirection.S)));
        scenario.Client.Prediction.Reset(Locked(new TileCoord(20, 20, 0), Target));
        Assert.Equal(2.0344439f, scenario.Client.LocalPose.Yaw, 5);
        Assert.Equal(1, nominations);

        scenario.Snapshot();
        scenario.Client.Prediction.Reset(Locked(new TileCoord(20, 20, 0), Target));
        Assert.Equal(MathF.PI, scenario.Client.LocalPose.Yaw, 5);
        Assert.Equal(1, nominations);

        bool Nominate(long id, TileRect footprint, int plane, out Vector2 aim)
        {
            nominations++;
            aim = new Vector2(24, 22);
            return true;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_gliding_body_uses_the_nomination_only_at_displayed_landing(bool interaction)
    {
        using var scenario = new TilePresentationScenario();
        TileMoveState body = Locked(new TileCoord(21, 20, 0), Target);
        body.StepFrom = new TileCoord(20, 20, 0);
        body.StepTotal = 4;
        body.StepTicks = 1;
        if (interaction)
        {
            body.CombatTarget = 0;
            body.InteractTarget = Target;
            body.InteractDomain = TileInteractionDomain.Entity;
        }
        TileMoveState target = TileMoveState.At(new TileCoord(21, 21, 0), TileDirection.S);
        target.FootprintSize = 2;
        scenario.Snapshot((Body, body), (Target, target));
        scenario.Client.EntityAimPointResolver = Nominate;
        scenario.Client.AdvancePresentation(.5f);

        Assert.True(scenario.Client.TryGetRemotePose(Body, out TilePose gliding));
        Assert.Equal(MathF.PI / 2f, gliding.Yaw, 5);
        scenario.Client.AdvancePresentation(.75f);
        Assert.True(scenario.Client.TryGetRemotePose(Body, out TilePose landed));
        Assert.Equal(MathF.PI, landed.Yaw, 5);

        bool Nominate(long id, TileRect footprint, int plane, out Vector2 aim)
        {
            aim = new Vector2(footprint.X, footprint.Z);
            return true;
        }
    }

    [Fact]
    public void Changing_entity_nomination_changes_only_the_presented_yaw()
    {
        using var scenario = new TilePresentationScenario();
        TileMoveState target = TileMoveState.At(new TileCoord(21, 20, 0), TileDirection.S);
        target.FootprintSize = 2;
        scenario.Snapshot((Target, target));
        TileMoveState attacker = Locked(new TileCoord(20, 20, 0), Target);
        attacker.Facing = TileDirection.E;
        scenario.Client.Prediction.Reset(attacker);
        var targets = new TileRemoteTargets(scenario.Client);
        var map = TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());
        var simulator = new TileMoveSimulator(map, new TileStepTicks(4, 2), combatTargets: targets);
        Assert.True(targets.TryGetFootprint(Target, out TileRect beforeFootprint, out int beforePlane));
        Assert.True(TileReach.Contains(map, beforeFootprint, beforePlane, attacker.Tile));
        TileMoveState before = scenario.Client.Prediction.PredictedState;
        byte[] encoded = Encode(before);
        TileMoveState next = simulator.Step(before, TileCommand.Continue(TileMoveMode.Walk), .25f);
        TilePose defaultPose = scenario.Client.LocalPose;
        scenario.Client.EntityAimPointResolver = Nominate;

        TilePose nominated = scenario.Client.LocalPose;
        Assert.Equal(defaultPose.Position, nominated.Position);
        Assert.Equal(2.3561945f, nominated.Yaw, 5);
        Assert.NotEqual(defaultPose.Yaw, nominated.Yaw);
        Assert.Equal(before, scenario.Client.Prediction.PredictedState);
        Assert.Equal(encoded, Encode(scenario.Client.Prediction.PredictedState));
        Assert.Equal(next, simulator.Step(before, TileCommand.Continue(TileMoveMode.Walk), .25f));
        Assert.True(targets.TryGetFootprint(Target, out TileRect afterFootprint, out int afterPlane));
        Assert.Equal(beforeFootprint, afterFootprint);
        Assert.Equal(beforePlane, afterPlane);
        Assert.True(TileReach.Contains(map, afterFootprint, afterPlane, attacker.Tile));

        bool Nominate(long id, TileRect footprint, int plane, out Vector2 aim)
        {
            aim = new Vector2(22, 22);
            return true;
        }
    }

    static TilePresentationScenario DelayedMovingTarget()
    {
        var scenario = new TilePresentationScenario();
        TileMoveState body = Locked(new TileCoord(20, 20, 0), Target);
        TileMoveState target = TileMoveState.At(new TileCoord(23, 22, 2), TileDirection.E);
        target.FootprintSize = 2;
        target.StepFrom = new TileCoord(22, 22, 2);
        target.StepTotal = 4;
        target.StepTicks = 1;
        scenario.Snapshot((Body, body), (Target, target));
        scenario.Client.AdvancePresentation(.5f);
        target.Tile = new TileCoord(25, 22, 2);
        target.StepFrom = new TileCoord(24, 22, 2);
        target.FootprintSize = 3;
        scenario.Snapshot((Body, body), (Target, target));
        scenario.Client.AdvancePresentation(0f);
        return scenario;
    }

    static TileMoveState Locked(TileCoord tile, long target)
    {
        TileMoveState state = TileMoveState.At(tile, TileDirection.N);
        state.CombatTarget = target;
        return state;
    }

    static byte[] Encode(TileMoveState state)
    {
        var world = new World();
        Entity entity = world.Spawn();
        world.Set(entity, new NetId(Body));
        world.Set(entity, state);
        return SnapshotWriter.Write(world, TileProtocol.CreateRegistry());
    }
}
