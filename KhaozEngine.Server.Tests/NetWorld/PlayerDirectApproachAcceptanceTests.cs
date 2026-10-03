using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;
using static KhaozEngine.Tests.NetWorld.PlayerRangeMovementTestRig;

namespace KhaozEngine.Tests.NetWorld;

public class PlayerDirectApproachAcceptanceTests
{
    private static readonly DirectApproachOptions Options = new(15, 0.1f, 45, 0.1f);

    [Fact]
    public void DirectWalkUpReachesRangeThroughTheRealCommandPath()
    {
        using var rig = new PlayerRangeMovementTestRig();
        var driver = new DirectMoveToRange(Options);
        ReachTarget target = ReachTarget.Box(new Vector3(2.5f, 0.75f, 0.6f), new Vector3(0.4f, 0.75f, 0.4f));
        const float Range = 0.3f;
        bool arrived = false;
        for (int tick = 0; tick < 60; tick++)
        {
            RangeSteering steering = rig.SteerDirect(driver, target, Range, targetMoves: false);
            if (steering.Status == RangeMoveStatus.InRange)
            {
                arrived = true;
                break;
            }
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            rig.Submit(PlayerPathMovement.Command(steering, false, 0.7f));
            PlayerMoveState authority = rig.Serve();
            Assert.Equal(authority.Position, rig.Client.LocalPredictedState.Position);
            rig.Ingest();
        }
        Assert.True(arrived);
        Assert.True(ReachGeometry.Within(Shape(rig.Authority), target, Range));
        Assert.True(ReachGeometry.Distance(Shape(rig.Authority), target) > Range - 0.1f);
        Assert.NotEmpty(rig.MoveFrames);
        foreach (byte[] envelope in rig.MoveFrames)
        {
            ReadOnlySpan<byte> body = SessionFrame.ReadBody(envelope);
            Assert.Equal(0x04, body[12] & 0x04);
        }
        rig.Drain();
    }

    [Fact]
    public void DirectWalkUpBlockedByAWallSubmitsIdle()
    {
        using var rig = new PlayerRangeMovementTestRig(wallAndBox: true);
        var driver = new DirectMoveToRange(Options);
        ReachTarget target = ReachTarget.Point(new Vector3(1.5f, 0.75f, 0f));
        int firstBlocked = -1;
        for (int tick = 0; tick < 90; tick++)
        {
            RangeSteering steering = rig.SteerDirect(driver, target, 0.3f, targetMoves: false);
            MoveCommand command = PlayerPathMovement.Command(steering, false, 0f);
            if (firstBlocked < 0 && steering.Status == RangeMoveStatus.Blocked) firstBlocked = rig.MoveFrames.Count;
            if (firstBlocked >= 0)
            {
                Assert.Equal(RangeMoveStatus.Blocked, steering.Status);
                Assert.Equal(Vector2.Zero, command.Move);
            }
            else
            {
                Assert.True(steering.Status is RangeMoveStatus.Following or RangeMoveStatus.Suspended);
            }
            rig.Submit(command);
            rig.Frame();
        }
        Assert.True(firstBlocked >= 15);
        Assert.True(rig.Authority.Position.X < 0f);
        for (int frame = firstBlocked; frame < rig.MoveFrames.Count; frame++)
        {
            Assert.True(MoveProtocol.TryDecodeMove(SessionFrame.ReadBody(rig.MoveFrames[frame]), out _, out MoveCommand sent));
            Assert.Equal(Vector2.Zero, sent.Move);
        }
        rig.Drain();
    }
}
