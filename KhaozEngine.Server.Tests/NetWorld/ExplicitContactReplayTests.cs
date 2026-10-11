using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.NetWorld;

public class ExplicitContactReplayTests
{
    const float Dt = 0.1f;
    static MoveTuning Tuning => MoveTuning.Default with
    {
        CapsuleRadius = 0.25f,
        CapsuleHalfHeight = 0.75f,
        WalkSpeed = 4,
        RunSpeed = 8,
        Gravity = 20,
        GroundedEpsilon = 0.05f,
        StepHeight = 0.4f,
        SwimSpeed = 3
    };
    static readonly WaterTraversalPolicy WaterPolicy = new(WaterTraversalMode.SurfaceSwimmer, 7);

    [Fact]
    public void R1BothServerHeadsAndCorrectionReplayAgreeAcrossGroundWaterAndAStep()
    {
        using var single = new Heads(sharded: false);
        using var sharded = new Heads(sharded: true);
        single.Pump(6);
        sharded.Pump(6);
        Assert.True(single.Client.Joined);
        Assert.True(sharded.Client.Joined);
        var initial = WireBasis(single.State);
        var commands = new MoveCommand[12];
        Array.Fill(commands, new(Vector2.UnitX, false, 0));
        PlayerMoveState correction = default;
        bool sawSwimming = false, sawLandAfterWater = false;
        for (int tick = 0; tick < commands.Length; tick++)
        {
            single.Client.SendInput(commands[tick]);
            sharded.Client.SendInput(commands[tick]);
            single.Pump(1);
            sharded.Pump(1);
            AssertMoveBits(WireBasis(single.State).Move, WireBasis(sharded.State).Move);
            Assert.Equal(WireBytes(single.State), WireBytes(sharded.State));
            Assert.Equal(WireBytes(single.State), WireBytes(single.Client.LocalPredictedState));
            Assert.Equal(WireBytes(sharded.State), WireBytes(sharded.Client.LocalPredictedState));
            Assert.Equal(0, single.Client.PendingPredictionCommands);
            Assert.Equal(0, sharded.Client.PendingPredictionCommands);
            sawSwimming |= single.State.Swimming;
            sawLandAfterWater |= sawSwimming && single.State.Grounded;
            if (tick == 0) correction = WireBasis(single.State);
        }
        Assert.True(sawSwimming);
        Assert.True(sawLandAfterWater);
        Assert.True(single.State.Grounded);
        Assert.InRange(single.State.Position.Y, 1.0009f, 1.0011f);
        Assert.Equal(WaterExcursionState.None, single.State.Move.WaterExcursion);

        using var replayEnvironment = new Shore();
        var simulator = replayEnvironment.Simulator();
        var prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(simulator,
            PredictionSettings.Default with { TickSeconds = Dt });
        var wrong = initial;
        wrong.Position += new Vector3(0.05f, 0, 0);
        prediction.Reset(wrong);
        int first = -1;
        using (simulator.BeginExplicitRead(wrong))
        {
            for (int tick = 0; tick < commands.Length; tick++)
            {
                int sequence = prediction.Predict(commands[tick]);
                if (tick == 0) first = sequence;
            }
        }
        float events = prediction.StepCumulativeY;
        Assert.InRange(events, 0.2495f, 0.2505f);
        int rebuilds = replayEnvironment.Environment.Acquisition.RebuildCalls;
        using (var read = simulator.BeginExplicitRead(correction))
        {
            Assert.True(read!.BasisValid);
            prediction.Reconcile(1, correction, first);
            Assert.Equal(commands.Length - 1, prediction.PendingCommandCount);
            Assert.Equal(events, prediction.StepCumulativeY);
        }
        Assert.True(replayEnvironment.Environment.Acquisition.RebuildCalls > rebuilds);
        var expected = correction;
        using (simulator.BeginExplicitRead(correction))
        {
            for (int tick = 1; tick < commands.Length; tick++) expected = simulator.Step(expected, commands[tick], Dt);
        }
        AssertMoveBits(expected.Move, prediction.PredictedState.Move);
        Assert.Equal(expected.FrameAnchor, prediction.PredictedState.FrameAnchor);
        Assert.Equal(14, MoveProtocol.WireProtocolVersion);
        Assert.Equal("contact-shell-footprint-swim-v1", ExplicitMovementProfile.ControllerModel);
    }

    static void AssertMoveBits(MoveState expected, MoveState actual)
    {
        foreach (var field in typeof(MoveState).GetFields(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            Assert.Equal(Bits(field.GetValue(expected)), Bits(field.GetValue(actual)));

        static object? Bits(object? value) => value switch
        {
            float scalar => BitConverter.SingleToInt32Bits(scalar),
            Vector2 vector => (BitConverter.SingleToInt32Bits(vector.X), BitConverter.SingleToInt32Bits(vector.Y)),
            Vector3 vector => (BitConverter.SingleToInt32Bits(vector.X), BitConverter.SingleToInt32Bits(vector.Y),
                BitConverter.SingleToInt32Bits(vector.Z)),
            _ => value
        };
    }

    static byte[] WireBytes(PlayerMoveState state)
    {
        var world = new World();
        var entity = world.Spawn();
        world.Set(entity, new NetId(0));
        world.Set(entity, ReplicatedPosition.FromWorld(state.Position, WorldFrame.Origin));
        world.Set(entity, MovementState.From(state));
        world.Set(entity, MovementOwnerState.From(state));
        return SnapshotWriter.WriteFiltered(world, MoveProtocol.CreateRegistry(), new HashSet<long> { 0 },
            ReplicationChannels.Replicate, ownerNetId: 0);
    }

    static PlayerMoveState WireBasis(PlayerMoveState state)
    {
        var world = new World();
        var view = new ClientReplicationView(MoveProtocol.CreateRegistry());
        view.Apply(world, WireBytes(state));
        Assert.True(view.TryGetEntity(0, out var entity));
        return PlayerMoveState.From(world.Get<ReplicatedPosition>(entity), world.Get<MovementState>(entity),
            world.Get<MovementOwnerState>(entity));
    }

    sealed class Shore : IDisposable
    {
        public AnalyticMovementEnvironment Environment { get; }
        public MovementEnvironmentContext Context { get; }
        public Shore()
        {
            Environment = new([new Room("room", new(new(-16), new(16)))],
                [new Water("lake", "room", new(new(0.5f, -8, -8), new(1, 1, 8)), 1)])
            { MissingContainment = false };
            Add(new(8, 0.125f, 8), new(0, -0.125f, 0), "floor");
            Add(new(2, 0.125f, 4), new(4, 0.125f, 0), "bank");
            using var initialized = Environment.Acquire(null, 4, 4);
            Environment.Acquisition.OnSupport = (in MovementSupportRequest request, Span<MovementSupportCandidate> candidates) =>
            {
                float top = request.Body.Centre.X >= 2 && request.Body.Centre.X <= 6 ? 0.25f : 0;
                candidates[0] = new(new("world", top > 0 ? "bank" : "floor"), Space("room"),
                    new(request.Body.Centre.X, top, request.Body.Centre.Z), Vector3.UnitY, null);
                return new(MovementAvailability.Known, 1, 1, AnalyticMovementEnvironment.Identity);
            };
            Context = new(Environment.View, Environment.Acquisition, AnalyticMovementEnvironment.Identity);
        }
        void Add(Vector3 half, Vector3 position, string name)
        {
            var handle = Environment.Physics.AddStatic(new BoxShape(half), Pose.At(position));
            Environment.RegisterBoxSupport(handle, name, "room");
        }
        public MovementQueryScope Scope(PlayerMoveState state, WorldFrame frame) =>
            new(new(-32), new(32), 4, 4, "world", null, AnalyticMovementEnvironment.Identity, new(frame, Environment.Physics.Origin, 1));
        public ExplicitPlayerMovement Settings => new(Context, Scope, WaterPolicy);
        public PlayerMoveSimulator Simulator() => new((_, _) => throw new InvalidOperationException("Legacy called."),
            Tuning, null, null, Environment.Physics, null, SamplerSpace.World, Settings);
        public void Dispose() => Environment.Dispose();
    }

    sealed class Heads : IDisposable
    {
        readonly Shore serverEnvironment = new();
        readonly Shore clientEnvironment = new();
        readonly INetTransport serverTransport;
        readonly WorldServer? single;
        readonly ShardedWorldServer? sharded;
        public WorldClient Client { get; }
        public Heads(bool sharded)
        {
            var (st, ct) = LoopbackTransport.CreatePair();
            serverTransport = st;
            float Legacy(float x, float z) => throw new InvalidOperationException("Legacy called.");
            if (sharded)
                this.sharded = new(st, new ShardedWorldServerConfig
                {
                    TickSeconds = Dt,
                    MaxPlayers = 2,
                    CellSize = 64,
                    OverlapMargin = 4,
                    InterestRadius = 2,
                    FrameAnchoring = false,
                    SpawnPosition = _ => new(0, 0.751f, 0),
                    PhysicsWorldFactory = _ => serverEnvironment.Environment.Physics,
                    ExplicitMovementFactory = _ => serverEnvironment.Settings
                }, Legacy, Tuning);
            else
                single = new(st, new WorldServerConfig
                {
                    TickSeconds = Dt,
                    MaxPlayers = 2,
                    InterestRadius = 100,
                    FrameAnchoring = false,
                    SpawnPosition = _ => new(0, 0.751f, 0),
                    ExplicitMovement = serverEnvironment.Settings
                }, Legacy, Tuning, physics: serverEnvironment.Environment.Physics);
            Client = new(ct, Legacy, Tuning, new WorldClientConfig
            {
                TickSeconds = Dt,
                FrameAnchoring = false,
                ExplicitMovement = clientEnvironment.Settings
            }, physics: clientEnvironment.Environment.Physics);
        }
        public PlayerMoveState State
        {
            get
            {
                PlayerMoveState state;
                bool found = single is not null ? single.TryGetPlayerState(0, out state)
                    : sharded!.TryGetPlayerState(0, out state);
                Assert.True(found);
                return state;
            }
        }
        public void Pump(int ticks)
        {
            for (int tick = 0; tick < ticks; tick++)
            {
                single?.Poll(); sharded?.Poll(); single?.Tick(Dt); sharded?.Tick(Dt); Client.Poll();
            }
        }
        public void Dispose()
        {
            Client.Dispose(); sharded?.Dispose(); serverTransport.Dispose();
            clientEnvironment.Dispose(); serverEnvironment.Dispose();
        }
    }
}
