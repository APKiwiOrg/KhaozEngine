using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.NetWorld.PlayerRangeMovementTestRig;

namespace KhaozEngine.Tests.NetWorld;

public class PlayerRangeMovementAcceptanceTests(ITestOutputHelper output)
{
    [Fact]
    public void FractionalWalkUpSurvivesTheRealClientServerPath()
    {
        using var rig = new PlayerRangeMovementTestRig();
        ReachTarget target = ReachTarget.Point(new Vector3(0f, 0.75f, -0.5001f));
        const float Range = 0.3f;
        Assert.False(ReachGeometry.Within(Shape(rig.Authority), target, Range));
        MoveCommand command = rig.Approach(target, Range);
        Assert.InRange(command.Move.Length(), float.Epsilon, 0.001f);
        int sequence = rig.Submit(command);
        PlayerMoveState authority = rig.Serve();
        Assert.Equal(authority.Position, rig.Client.LocalPredictedState.Position);
        rig.Ingest();

        byte[] envelope = Assert.Single(rig.MoveFrames);
        Assert.Equal(19, envelope.Length);
        ReadOnlySpan<byte> body = SessionFrame.ReadBody(envelope);
        Assert.Equal(18, body.Length);
        Assert.Equal(0x04, body[12] & 0x04);
        Assert.True(MoveProtocol.TryDecodeMove(body, out int decodedSequence, out MoveCommand decoded));
        Assert.Equal(sequence, decodedSequence);
        Assert.Equal(command.Move, decoded.Move);
        Assert.True(decoded.ScaleSpeedByAxis);
        Assert.False(decoded.Jump);
        Assert.False(decoded.FaceCamera);
        Assert.InRange(rig.Authority.Position.Z, -0.000101f, -0.000099f);
        Assert.True(ReachGeometry.Within(Shape(rig.Authority), target, Range));
        Assert.InRange(ReachGeometry.Distance(Shape(rig.Authority), target), 0.299999f, Range);
        Assert.Equal(RangeMoveStatus.InRange, rig.Steer(target, Range).Status);
        MoveCommand arrived = rig.Approach(target, Range);
        Assert.Equal(Vector2.Zero, arrived.Move);
        Vector3 stopped = rig.Authority.Position;
        rig.Submit(arrived);
        rig.Frame();
        rig.Stop();
        rig.Frame();
        Assert.Equal(stopped, rig.Authority.Position);
        rig.Drain();
    }

    [Fact]
    public void SolidBoxWalkUpUsesTheRealWallDetour()
    {
        using var rig = new PlayerRangeMovementTestRig(wallAndBox: true);
        ReachTarget target = ReachTarget.Box(new Vector3(0.75f, 0.75f, -0.5f), new Vector3(0.4f, 0.75f, 0.4f));
        bool crossedAboveWall = false;
        bool arrived = false;
        for (int tick = 0; tick < 160; tick++)
        {
            RangeSteering steering = rig.Steer(target, 0.3f);
            if (steering.Status == RangeMoveStatus.InRange) { arrived = true; break; }
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            Vector3 previous = rig.Authority.Position;
            rig.Submit(PlayerPathMovement.Command(steering, false, MathF.PI / 2f));
            PlayerMoveState authority = rig.Serve();
            Assert.Equal(authority.Position, rig.Client.LocalPredictedState.Position);
            rig.Ingest();
            Vector3 current = authority.Position;
            Assert.True(rig.Navigation.AllowsSegment(previous - Vector3.UnitY * 0.75f, current - Vector3.UnitY * 0.75f));
            if (previous.X < 0f && current.X >= 0f)
            {
                Assert.True(previous.Z >= 0.4f && current.Z >= 0.4f);
                crossedAboveWall = true;
            }
            AssertOutsideRectangle(current, -0.025f, 0.025f, -1.2f, 0.2f);
            AssertOutsideRectangle(current, 0.35f, 1.15f, -0.9f, -0.1f);
            rig.AssertClear(authority);
        }
        Assert.True(arrived);
        Assert.True(crossedAboveWall);
        Assert.True(ReachGeometry.Within(Shape(rig.Authority), target, 0.3f));
        Assert.InRange(ReachGeometry.Distance(Shape(rig.Authority), target), 0.29999f, 0.3f);
        Vector3 stopped = rig.Authority.Position;
        rig.Stop();
        Assert.Equal(stopped, rig.Authority.Position);
        rig.Drain();
    }

    [Fact]
    public void ChaseReevaluatesTheMovingCapsuleEveryTick()
    {
        using var rig = new PlayerRangeMovementTestRig();
        ReachTarget target = default;
        bool arrived = false;
        for (int tick = 0; tick < 80; tick++)
        {
            float drift = MathF.Min(tick, 20) * 0.015f;
            Vector3 targetCentre = new(1.25f, 0.5f, -drift);
            target = ReachTarget.Capsule(new MovementBody(targetCentre, 0.2f, 0.5f));
            RangeSteering steering = rig.Steer(target, 0.3f);
            if (tick < 3 || tick >= 76)
                output.WriteLine($"Chase tick {tick}: body {rig.Client.LocalPredictedState.Position}, target {targetCentre}, steering {steering}, distance {ReachGeometry.Distance(Shape(rig.Authority), target):G9}");
            if (steering.Status == RangeMoveStatus.InRange && tick >= 20) { arrived = true; break; }
            Assert.True(steering.Status is RangeMoveStatus.Following or RangeMoveStatus.InRange);
            rig.Submit(PlayerPathMovement.Command(steering, false, -MathF.PI / 2f));
            PlayerMoveState authority = rig.Serve();
            Assert.Equal(authority.Position, rig.Client.LocalPredictedState.Position);
            rig.Ingest();
        }
        Assert.True(arrived);
        Assert.True(ReachGeometry.Within(Shape(rig.Authority), target, 0.3f));
        Assert.InRange(ReachGeometry.Distance(Shape(rig.Authority), target), 0.29999f, 0.3f);
        ReachTarget movedAfterArrival = ReachTarget.Capsule(new MovementBody(new Vector3(1.25f, 0.5f, -1.2f), 0.2f, 0.5f));
        Assert.False(ReachGeometry.Within(Shape(rig.Authority), movedAfterArrival, 0.3f, 0.01f));
        Assert.Equal(RangeMoveStatus.Following, rig.Steer(movedAfterArrival, 0.3f).Status);
        rig.Stop();
        rig.Drain();
    }

    [Fact]
    public void TargetHeightPreventsFalseArrivalAndHorizontalInput()
    {
        using var rig = new PlayerRangeMovementTestRig();
        ReachTarget target = ReachTarget.Capsule(new MovementBody(new Vector3(0f, 3f, 0f), 0.2f, 0.5f));
        Vector3 position = rig.Authority.Position;
        for (int tick = 0; tick < 3; tick++)
        {
            Assert.False(ReachGeometry.Within(Shape(rig.Client.LocalPredictedState), target, 0.3f));
            Assert.Equal(RangeMoveStatus.Unreachable, rig.Steer(target, 0.3f).Status);
            MoveCommand command = rig.Approach(target, 0.3f);
            Assert.Equal(Vector2.Zero, command.Move);
            rig.Submit(command);
            rig.Frame();
        }
        Assert.Equal(position, rig.Authority.Position);
        Assert.False(ReachGeometry.Within(Shape(rig.Authority), target, 0.3f));
        rig.Drain();
    }

    [Fact]
    public void GameCancellationOnTargetDeathSubmitsIdle()
    {
        using var rig = new PlayerRangeMovementTestRig();
        ReachTarget target = ReachTarget.Point(new Vector3(1.5f, 0.75f, 0f));
        rig.Submit(rig.Approach(target, 0.3f));
        rig.Frame();
        Assert.True(rig.Authority.Position.X > 0f);
        bool targetAlive = false;
        rig.Mover.Reset();
        RangeSteering cancelled = targetAlive ? rig.Steer(target, 0.3f) : new(Vector2.Zero, RangeMoveStatus.InRange);
        MoveCommand command = PlayerPathMovement.Command(cancelled, false, 0f);
        Assert.Equal(Vector2.Zero, command.Move);
        Vector3 stopped = rig.Authority.Position;
        rig.Submit(command);
        rig.Frame();
        rig.Stop();
        Assert.Equal(stopped, rig.Authority.Position);
        rig.Drain();
    }

    [Fact]
    public void ManualOverrideThenResetAndIdleDoesNotRetainThePathCommand()
    {
        using var rig = new PlayerRangeMovementTestRig();
        ReachTarget target = ReachTarget.Point(new Vector3(0f, 0.75f, -1.5f));
        rig.Submit(rig.Approach(target, 0.3f));
        rig.Frame();
        Vector3 beforeManual = rig.Authority.Position;
        rig.Submit(new MoveCommand(Vector2.UnitX, false, 0f));
        rig.Frame();
        Vector3 afterManual = rig.Authority.Position;
        Assert.Equal(beforeManual.X + 0.2f, afterManual.X, 6);
        Assert.Equal(beforeManual.Z, afterManual.Z);
        rig.Mover.Reset();
        rig.Stop();
        rig.Frame();
        Assert.Equal(afterManual, rig.Authority.Position);
        Assert.Equal(afterManual, rig.Client.LocalPredictedState.Position);
        ReachTarget replacement = ReachTarget.Point(new Vector3(1.5f, 0.75f, afterManual.Z));
        MoveCommand fresh = rig.Approach(replacement, 0.3f);
        Assert.True(CharacterMovement.CameraRelativeDir(fresh).X > 0f);
        rig.Drain();
    }

    [Fact]
    public void SimulationStateGuidesUnackedFractionsThroughARealCorrection()
    {
        using var rig = new PlayerRangeMovementTestRig();
        MoveCommand initial = rig.Approach(ReachTarget.Point(new Vector3(0f, 0.75f, -0.7f)), 0.3f);
        output.WriteLine($"Initial body {rig.Client.LocalPredictedState.Position}, command {initial.Move}");
        int first = rig.Submit(initial);
        Assert.NotEqual(rig.Client.LocalPredictedState.Position, rig.Client.LocalRenderState.Position);
        rig.Server.SetSpeedScale(PlayerRef.Slot(0), 0.5f);
        PlayerMoveState basis = rig.Serve();
        MoveCommand small = rig.Approach(ReachTarget.Point(new Vector3(0f, 0.75f, -0.75f)), 0.3f);
        Assert.InRange(small.Move.Length(), 0.01f, 0.99f);
        rig.Submit(small);
        MoveCommand larger = rig.Approach(ReachTarget.Point(new Vector3(0f, 0.75f, -0.85f)), 0.3f);
        Assert.InRange(larger.Move.Length(), small.Move.Length(), 0.99f);
        rig.Submit(larger);
        PlayerMoveState expectedReplay = ReplayOnFlatWorld(basis, small, larger);
        Vector3 beforeCorrection = rig.Client.LocalPredictedState.Position;
        output.WriteLine($"Basis {basis.Position}, small {small.Move}, larger {larger.Move}, expected replay {expectedReplay.Position}");
        foreach (byte[] envelope in rig.MoveFrames)
        {
            Assert.Equal(19, envelope.Length);
            ReadOnlySpan<byte> body = SessionFrame.ReadBody(envelope);
            Assert.Equal(18, body.Length);
            Assert.Equal(0x04, body[12] & 0x04);
        }
        rig.Client.AdvancePresentation(TickSeconds);
        Assert.Equal(-1, rig.LastAcknowledgedSequence);
        rig.Ingest();

        Assert.Equal(first, rig.LastAcknowledgedSequence);
        Assert.Equal(Vector3.Distance(beforeCorrection, expectedReplay.Position), rig.Client.NetStats.LastCorrectionMeters, 6);
        Assert.True(rig.Client.NetStats.LastCorrectionMeters > 0.05f);
        Assert.Equal(expectedReplay.Position, rig.Client.LocalPredictedState.Position);
        Assert.Equal(0.5f, rig.Client.LocalPredictedState.Move.SpeedScale);
        Assert.NotEqual(rig.Client.LocalPredictedState.Position, rig.Client.LocalRenderState.Position);
        ReachTarget next = ReachTarget.Point(new Vector3(0f, 0.75f, -0.8f));
        Assert.True(ReachGeometry.Within(Shape(rig.Client.LocalRenderState), next, 0.3f));
        Assert.False(ReachGeometry.Within(Shape(rig.Client.LocalPredictedState), next, 0.3f));
        RangeSteering steering = rig.Steer(next, 0.3f);
        Assert.Equal(RangeMoveStatus.Following, steering.Status);
        MoveCommand command = PlayerPathMovement.Command(steering, false, 0f);
        Assert.True(command.Move.Length() > 0f);
        rig.Submit(command);
        rig.Drain();
        Assert.Equal(rig.Authority.Position, rig.Client.LocalPredictedState.Position);
        Assert.Equal(0.5f, rig.Authority.Move.SpeedScale);
        rig.Stop();
        rig.Drain();
    }

    static void AssertOutsideRectangle(Vector3 position, float minX, float maxX, float minZ, float maxZ)
    {
        float dx = position.X - Math.Clamp(position.X, minX, maxX);
        float dz = position.Z - Math.Clamp(position.Z, minZ, maxZ);
        Assert.True(dx * dx + dz * dz > 0.2f * 0.2f, $"Capsule intersects obstacle at {position}");
    }
}
