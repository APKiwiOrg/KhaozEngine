using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class GroundMovementCodecTests
{
    [Theory]
    [InlineData(MovementCommitmentPhase.Preparing, 3.5f, 0)]
    [InlineData(MovementCommitmentPhase.Travelling, 2.25f, 1)]
    [InlineData(MovementCommitmentPhase.Travelling, 0f, 0)] // exhausted budget holds until the next tick
    [InlineData(MovementCommitmentPhase.Recovering, 0f, 2)]
    public void Ground_state_survives_wire_basis_frames_and_handoff(
        MovementCommitmentPhase phase, float remaining, byte blocked)
    {
        var source = new PlayerMoveState
        {
            Move = new MoveState
            {
                Position = new Vector3(3, 4, 5),
                Grounded = true,
                SpeedScale = 1.099609375f,
                Commitment = MovementCommitment.ForGroundTravel(71, new Vector2(3, 4), 7.5f, 4f,
                    preparationSeconds: 0.125f, recoverySeconds: 0.25f, timeoutSeconds: 3.75f) with
                {
                    Phase = phase,
                    DistanceRemaining = remaining,
                    ConsecutiveBlockedTicks = blocked,
                    EndReason = phase == MovementCommitmentPhase.Recovering
                        ? MovementCommitmentEndReason.Arrived : MovementCommitmentEndReason.None,
                },
            },
            FrameAnchor = new Vector2(512, -512),
        };
        MovementState movement = MovementState.From(source);
        var position = ReplicatedPosition.InFrame(new WorldFrame(4, -4), source.Position);
        var owner = MovementOwnerState.From(source);
        Assert.Equal(source.Move.Commitment, PlayerMoveState.From(source.Position, movement, owner).Move.Commitment);
        Assert.Equal(source.Move.Commitment, PlayerMoveState.From(position, movement, owner).Move.Commitment);
        Assert.Equal(source.Move.Commitment, source.ToAnchor(new Vector2(-512, 512)).Move.Commitment);
        Assert.Equal(source.Move.Commitment, source.Absolute.Move.Commitment);

        // Migrate is the real component capture channel used by a cell handoff.
        foreach (ReplicationChannels channel in new[] { ReplicationChannels.Replicate, ReplicationChannels.Migrate,
                     ReplicationChannels.Persist })
        {
            var world = new World();
            Entity entity = world.Spawn();
            world.Set(entity, new NetId(71));
            world.Set(entity, movement);
            world.Set(entity, owner);
            world.Set(entity, position);
            byte[] bytes = SnapshotWriter.Write(world, MoveProtocol.CreateRegistry(), channel);
            if (channel == ReplicationChannels.Migrate)
            {
                var cell = new CellSim(new CellCoord(9, 9), 1f / 30f, MoveProtocol.CreateRegistry(), 64f);
                Assert.Equal(71L, Assert.Single(cell.AdoptFromMigrate(bytes)));
                Assert.True(cell.TryGetOwned(71, out Entity adopted));
                Assert.Equal(source.Move.Commitment, cell.World.Get<MovementState>(adopted).Commitment);
                Assert.Equal(owner, cell.World.Get<MovementOwnerState>(adopted));
            }
            var destination = new World();
            var view = new ClientReplicationView(MoveProtocol.CreateRegistry());
            view.Apply(destination, bytes);
            Assert.True(view.TryGetEntity(71, out Entity restored));
            MovementState received = destination.Get<MovementState>(restored);
            Assert.Equal(movement.Commitment, received.Commitment);
            Assert.Equal(movement.SpeedScaleQ, received.SpeedScaleQ);
            PlayerMoveState basis = PlayerMoveState.From(destination.Get<ReplicatedPosition>(restored), received, owner);
            Assert.Equal(source.Move.Commitment, basis.Move.Commitment);
            Assert.Equal(source.Absolute.Position, basis.Absolute.Position);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    public void Truncated_ground_suffix_is_refused(int missingBytes)
    {
        byte[] payload = CellBlobFixtures.Movement(15, new MovementState
        {
            Commitment = MovementCommitment.ForGroundTravel(1, Vector2.UnitX, 2, 4),
        });
        byte[] body = new CellBlobFixtures.BodyBuilder().Entity(1,
            (MoveProtocol.MovementTypeId, payload[..^missingBytes])).ToBody();
        var view = new ClientReplicationView(MoveProtocol.CreateRegistry());
        Assert.False(view.TryApply(new World(), body, out _));
    }
}
