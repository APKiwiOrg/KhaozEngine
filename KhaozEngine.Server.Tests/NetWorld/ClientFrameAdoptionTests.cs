using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Netcode;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The client half of the floating-origin wire: the frame is ADOPTED from the server rather than derived, a shift
/// mid-replay manufactures no correction and glides nothing, and every position the client hands a consumer is
/// absolute world metres.
/// </summary>
public class ClientFrameAdoptionTests
{
    private const float Dt = 1f / 60f;
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;
    private static readonly MoveTuning Unit = MoveTuning.Default;
    private static readonly Vector3 Far = new(100_000f, 0f, 100_000f);
    private static readonly PredictionSettings Settings = PredictionSettings.Default with { TickSeconds = Dt };

    private static ClientPrediction<PlayerMoveState, MoveCommand> Predictor(PlayerMoveSimulator simulator) =>
        new(simulator, Settings);

    private static PlayerMoveSimulator Simulator() => new(Flat, Unit);

    private static PlayerMoveState Seed(Vector3 absolute, Vector2 anchor) =>
        new PlayerMoveState { Position = absolute, Grounded = true }.ToAnchor(anchor);

    [Fact]
    public void Test15_The_client_adopts_the_servers_anchor_rather_than_deriving_one()
    {
        // The failure this rules out: a client that computes its own anchor sits one tick out of step with the
        // server across a re-anchor, and for that tick the two heads are 128 m apart. The stamp is authoritative
        // state, exactly like position.
        PlayerMoveSimulator sim = Simulator();
        ClientPrediction<PlayerMoveState, MoveCommand> prediction = Predictor(sim);

        var chosen = new Vector2(WorldFrame.Grid * 781f, WorldFrame.Grid * 781f);
        prediction.Reset(Seed(Far, chosen));

        // A basis whose anchor is deliberately NOT the one the client's own position would round to.
        var serverAnchor = new Vector2(WorldFrame.Grid * 780f, WorldFrame.Grid * 782f);
        Assert.NotEqual(chosen, serverAnchor);
        PlayerMoveState basis = Seed(Far, serverAnchor);

        prediction.Reconcile(0, basis, lastAcknowledgedSeq: -1);

        Assert.Equal(serverAnchor, prediction.PredictedState.FrameAnchor);
        // And the conversion was a change of coordinates, not of position: the absolute world position is unmoved.
        Assert.True(Vector3.Distance(prediction.PredictedState.Absolute.Position, Far) < 0.001f);
    }

    [Fact]
    public void Test14_A_frame_shift_manufactures_no_correction_and_no_render_glide()
    {
        // Two bugs in one place, and this asserts both. Without the conversion at the top of Reconcile, `planarError`
        // measures the whole 128 m anchor delta and trips the HardSnapDistance gate (a hard cut on a shift that
        // moved nothing), and even past the gate the C1 branch re-anchors renderOffset against a rendered position
        // captured in the OLD frame and glides the avatar a frame-width across the screen while it decays.
        var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f);
        var anchorA = new Vector2(WorldFrame.Grid * 781f, WorldFrame.Grid * 781f);
        var anchorB = new Vector2(WorldFrame.Grid * 782f, WorldFrame.Grid * 781f);   // one grid step along X

        // Control: the same scenario with no frame change at all.
        (ReconciliationResult control, Vector3 controlRenderBefore, Vector3 controlRenderAfter, Vector3 controlSettled) =
            Run(anchorA, anchorA);
        // Under test: the basis arrives in the next frame along.
        (ReconciliationResult shifted, Vector3 renderBefore, Vector3 renderAfter, Vector3 settled) =
            Run(anchorA, anchorB);

        Assert.False(shifted.HardSnapApplied);
        Assert.False(control.HardSnapApplied);
        Assert.Equal(control.PositionError, shifted.PositionError, 4);

        // The rendered ABSOLUTE position is continuous across the reconcile: the render offset did not absorb the
        // anchor delta, which would have shown up here as a 128 m jump.
        Assert.True(Vector3.Distance(renderBefore, renderAfter) < 0.05f,
            $"the rendered position jumped {Vector3.Distance(renderBefore, renderAfter):F3} m across the shift");

        // And the shift is invisible in the strongest sense available: the shifted run tracks the no-shift control
        // frame for frame, both at the reconcile and after the whole smoothing window has decayed. A bare bound on
        // the glide would be the wrong assertion, because the control glides too - the inter-tick interpolation
        // legitimately carries the render forward to the predicted position either way.
        const float Ulp = 0.02f;   // a couple of float32 ULPs at 100 km, which is what these positions are made of
        Assert.True(Vector3.Distance(renderBefore, controlRenderBefore) < Ulp);
        Assert.True(Vector3.Distance(renderAfter, controlRenderAfter) < Ulp,
            $"the shifted run rendered {Vector3.Distance(renderAfter, controlRenderAfter):F3} m from the control");
        Assert.True(Vector3.Distance(settled, controlSettled) < Ulp,
            $"after the smoothing window the shifted run sat {Vector3.Distance(settled, controlSettled):F3} m from "
          + "the control: the shift glided the avatar.");

        (ReconciliationResult, Vector3, Vector3, Vector3) Run(Vector2 predictedAnchor, Vector2 basisAnchor)
        {
            PlayerMoveSimulator sim = Simulator();
            sim.Frame = new WorldFrame((short)(predictedAnchor.X / WorldFrame.Grid), (short)(predictedAnchor.Y / WorldFrame.Grid));
            ClientPrediction<PlayerMoveState, MoveCommand> prediction = Predictor(sim);
            prediction.Reset(Seed(Far, predictedAnchor));
            for (int i = 0; i < 8; i++) { prediction.Predict(forward); prediction.AdvancePresentation(Dt); }

            Vector3 before = prediction.RenderedState.Absolute.Position;

            // The island moves as one: the simulator is re-pointed at the basis's frame before the replay, exactly as
            // WorldClient does when it adopts a stamp off the wire.
            sim.Frame = new WorldFrame((short)(basisAnchor.X / WorldFrame.Grid), (short)(basisAnchor.Y / WorldFrame.Grid));
            PlayerMoveState basis = prediction.PredictedState.Absolute.ToAnchor(basisAnchor);
            ReconciliationResult rr = prediction.Reconcile(1, basis, lastAcknowledgedSeq: -1);

            Vector3 after = prediction.RenderedState.Absolute.Position;
            for (int i = 0; i < 60; i++) prediction.AdvancePresentation(Dt);
            return (rr, before, after, prediction.RenderedState.Absolute.Position);
        }
    }

    [Fact]
    public void A_state_that_never_opted_into_frames_never_reaches_the_throwing_wither()
    {
        // The DIM contract: WithFrameAnchor's default throws, and it is unreachable unless the two anchors actually
        // differ, which is impossible for a state that left FrameAnchor at its default.
        var prediction = new ClientPrediction<PlainState, int>(new PlainSimulator(), Settings);
        prediction.Reset(new PlainState { X = 1f });
        prediction.Predict(1);
        ReconciliationResult rr = prediction.Reconcile(0, new PlainState { X = 2f }, lastAcknowledgedSeq: -1);
        Assert.False(rr.HardSnapApplied);

        // And a state that DID opt in on one side only says so loudly rather than silently dropping the conversion.
        Assert.Throws<NotSupportedException>(() =>
        {
            var half = new ClientPrediction<HalfFramedState, int>(new HalfFramedSimulator(), Settings);
            half.Reset(new HalfFramedState { X = 1f, Anchor = new Vector2(128f, 0f) });
            half.Reconcile(0, new HalfFramedState { X = 1f, Anchor = Vector2.Zero }, lastAcknowledgedSeq: -1);
        });
    }

    [Fact]
    public void Test23_The_WorldClient_presentation_surface_is_uniformly_absolute()
    {
        // The bug this rules out produces no compile error and no exception: the local avatar comes out of prediction
        // frame-local while every remote comes out of ReplicatedPosition.Value absolute, both as a Vector3, both in
        // the same EntityRenderState list. The avatar simply renders an anchor delta away from the world.
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig
        {
            TickSeconds = Dt,
            InterestRadius = 500f,
            MaxPlayers = 8,
            FrameAnchoring = true,
            SpawnPosition = _ => Far,
        };
        var server = new WorldServer(st, config, Flat, Unit);
        var client = new WorldClient(ct, Flat, Unit, new WorldClientConfig { TickSeconds = Dt });
        using (client)
        {
            for (int i = 0; i < 10 && !client.Joined; i++) { server.Poll(); server.Tick(Dt); client.Poll(); }
            Assert.True(client.Joined);

            Vector3 remoteAt = Far + new Vector3(11.25f, 0f, -6.5f);
            server.SpawnEntity(remoteAt.X, remoteAt.Z);
            for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(Dt); client.Poll(); }

            // The server really is framed, and the client really did adopt that frame - or the rest proves nothing.
            Assert.NotEqual(WorldFrame.Origin, server.IslandFrame);
            Assert.Equal(server.IslandFrame, client.IslandFrame);

            IReadOnlyList<EntityRenderState> snapshot = client.Snapshot();
            EntityRenderState local = Assert.Single(snapshot, e => e.IsLocal);
            EntityRenderState remote = Assert.Single(snapshot, e => !e.IsLocal);

            Assert.True(Vector3.Distance(new Vector3(local.Position.X, 0f, local.Position.Z),
                                         new Vector3(Far.X, 0f, Far.Z)) < 1f,
                $"the local avatar rendered at {local.Position}, not at its absolute world position {Far}");
            Assert.True(Vector3.Distance(new Vector3(remote.Position.X, 0f, remote.Position.Z),
                                         new Vector3(remoteAt.X, 0f, remoteAt.Z)) < 0.01f,
                $"the remote rendered at {remote.Position}, not at its absolute world position {remoteAt}");

            // The second assertion is what stops the two surfaces drifting apart later: fixing only Snapshot() is
            // the natural half-fix.
            PlayerMoveState render = client.LocalRenderState;
            Assert.Equal(Vector2.Zero, render.FrameAnchor);
            Assert.Equal(local.Position, render.Position);
        }
    }

    // A predicted state with no frame concept at all: FrameAnchor stays at its default, so WithFrameAnchor's
    // throwing default is unreachable.
    private struct PlainState : IPredictedState<PlainState>
    {
        public float X;
        public readonly Vector2 Position => new(X, 0f);
        public readonly PlainState WithPosition(Vector2 position) => new() { X = position.X };
    }

    private sealed class PlainSimulator : ITickSimulator<PlainState, int>
    {
        public PlainState Step(in PlainState state, in int command, float dt) => new() { X = state.X + command * dt };
    }

    // A state that opted into FrameAnchor and did NOT implement the wither: the one shape the throwing default is
    // there to catch.
    private struct HalfFramedState : IPredictedState<HalfFramedState>
    {
        public float X;
        public Vector2 Anchor;
        public readonly Vector2 Position => new(X, 0f);
        public readonly Vector2 FrameAnchor => Anchor;
        public readonly HalfFramedState WithPosition(Vector2 position) => new() { X = position.X, Anchor = Anchor };
    }

    private sealed class HalfFramedSimulator : ITickSimulator<HalfFramedState, int>
    {
        public HalfFramedState Step(in HalfFramedState state, in int command, float dt) =>
            new() { X = state.X + command * dt, Anchor = state.Anchor };
    }
}
