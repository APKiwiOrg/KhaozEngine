using System;
using System.Numerics;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// A REMOTE'S CLICKED FIRST STEP, through a real <see cref="TileWorldServer"/> and a real
/// <see cref="TileWorldClient"/> over an in-memory transport, at the issue's own measurement: a quarter second
/// tick drawn at 60 fps. <see cref="TileMoveSimulator"/> spends the click tick on the step itself, so a step that
/// starts from a standing body reads one tick in on the tick it commits. The client tells that door from the
/// landing door off the sample before it and hands the presenter the answer, so the first step of a clicked route
/// starts at fraction zero and plays over the ticks it actually has. The arithmetic is pinned in
/// <see cref="TileClickDoorTests"/>. What is pinned HERE is the detection through the real wiring, and the reads a
/// head draws with.
/// </summary>
public class TileClickDoorLoopTests
{
    const float Tick = 0.25f;
    const float Frame = 1f / 60f;

    // Frames from the one a remote's committed tile flips onto a new step, all drawn off that step's first sample.
    // A sample lasts a tick, fifteen frames here, so ten is inside it whatever the phase.
    const int Window = 10;

    // The flagged step's own per-frame rate, in tiles: the step's tile over the ticks it actually has, which is
    // one fewer than its total and never fewer than one. N/(N-1) of an ordinary frame, and exactly one at N = 1.
    static float ClickRate(int n) => Frame / Tick / Math.Max(1, n - 1);

    static float OrdinaryRate(int n) => Frame / Tick / n;

    [Theory]
    [InlineData(4, 2, TileMoveMode.Walk)]
    [InlineData(4, 2, TileMoveMode.Run)]
    [InlineData(1, 1, TileMoveMode.Walk)]
    public void A_clicked_remote_starts_its_first_step_without_a_jump(byte walk, byte run, TileMoveMode mode)
    {
        var cadence = new TileStepTicks(walk, run);
        int n = cadence.For(mode);
        using var loop = new RemoteLoop(cadence);
        var start = new TileCoord(12, 10, 0);
        long remote = loop.SpawnRemote(start);

        loop.Click(new TileCoord(12, 16, 0), mode);
        (float flip, float worst) = loop.ClickWindow(remote, start);

        // Nothing moves on the frame the step commits: the body is still on the tile it is leaving.
        Assert.True(flip < 1e-5f, $"the remote moved {flip} tiles on the frame its clicked step committed");
        Assert.True(Math.Abs(worst - ClickRate(n)) < 1e-4f,
            $"the clicked step's worst frame moved {worst} tiles, not its own rate {ClickRate(n)} "
            + $"(an ordinary frame is {OrdinaryRate(n)})");
    }

    [Fact]
    public void A_remote_stopped_and_clicked_again_starts_each_route_without_a_jump()
    {
        var cadence = new TileStepTicks(4, 2);
        using var loop = new RemoteLoop(cadence);
        var start = new TileCoord(12, 10, 0);
        var stop = new TileCoord(12, 12, 0);
        long remote = loop.SpawnRemote(start);

        loop.Click(stop, TileMoveMode.Walk);
        (float firstFlip, float first) = loop.ClickWindow(remote, start);
        // Two steps of four ticks, the delayed timeline, and a rest long enough to be read as standing.
        loop.Frames(180);
        Assert.True(loop.Client.TryGetRemoteTile(remote, out TileCoord stood));
        Assert.Equal(stop, stood);
        Assert.True(loop.Client.TryGetRemoteStepProgress(remote, out float rest));
        Assert.Equal(1f, rest);

        loop.Click(new TileCoord(12, 15, 0), TileMoveMode.Walk);
        (float secondFlip, float second) = loop.ClickWindow(remote, stop);

        Assert.True(firstFlip < 1e-5f && secondFlip < 1e-5f, $"a click frame moved: {firstFlip}, {secondFlip}");
        Assert.True(Math.Abs(first - ClickRate(4)) < 1e-4f, $"first route's worst frame {first}");
        Assert.True(Math.Abs(second - ClickRate(4)) < 1e-4f, $"second route's worst frame {second}");
    }

    /// <summary>
    /// A re-click while moving is the LANDING door, not the click door: the step in flight is never abandoned, so
    /// the new route's first step starts on the tick that step lands, at zero progress, and the presenter draws it
    /// exactly as it always did. Measured on the step PROGRESS, which is what draw priority reads, so the pace is
    /// read the same way whatever direction the re-path turned.
    /// </summary>
    [Fact]
    public void A_remote_repathed_while_moving_keeps_the_ordinary_pace()
    {
        var cadence = new TileStepTicks(4, 2);
        using var loop = new RemoteLoop(cadence);
        long remote = loop.SpawnRemote(new TileCoord(12, 10, 0));
        loop.Click(new TileCoord(12, 30, 0), TileMoveMode.Walk);
        loop.Frames(150);
        Assert.True(loop.Client.TryGetRemoteStepProgress(remote, out float moving));
        Assert.True(moving < 1f, "the remote was not walking when the re-click landed");

        loop.Click(new TileCoord(30, 20, 0), TileMoveMode.Walk);
        int frames = 0;
        while (loop.Client.TryGetRemoteTile(remote, out TileCoord tile) && tile.X == 12)
        {
            Assert.True(++frames < 200, "the re-pathed route never turned off the column it was walking");
            loop.Step();
        }

        Assert.True(loop.Client.TryGetRemoteStepProgress(remote, out float previous));
        Assert.Equal(0f, previous);
        for (int i = 1; i < Window; i++)
        {
            loop.Step();
            Assert.True(loop.Client.TryGetRemoteStepProgress(remote, out float now));
            Assert.True(Math.Abs(now - previous - OrdinaryRate(4)) < 1e-4f,
                $"frame {i} of the re-pathed step advanced {now - previous}, not the ordinary {OrdinaryRate(4)}");
            previous = now;
        }
    }

    [Fact]
    public void A_teleported_remote_cuts_and_its_next_click_is_the_click_door()
    {
        var cadence = new TileStepTicks(4, 2);
        using var loop = new RemoteLoop(cadence);
        var start = new TileCoord(12, 10, 0);
        long remote = loop.SpawnRemote(start);
        loop.Click(new TileCoord(12, 20, 0), TileMoveMode.Walk);
        loop.ClickWindow(remote, start);

        // Mid way through the clicked step, the server places the body somewhere else.
        var placed = new TileCoord(16, 14, 0);
        loop.Server.SetPlayerState(1, TileMoveState.At(placed, TileDirection.N), teleport: true);
        Assert.True(loop.Client.TryGetRemotePose(remote, out TilePose before));
        int frames = 0;
        while (!(loop.Client.TryGetRemoteTile(remote, out TileCoord tile) && tile.Equals(placed)))
        {
            Assert.True(++frames < 120, "the teleport never reached the delayed timeline");
            Assert.True(loop.Client.TryGetRemotePose(remote, out before));
            loop.Step();
        }

        // A cut: on its new tile at rest on the frame it lands, with nothing drawn across the ground in between.
        Assert.True(loop.Client.TryGetRemotePose(remote, out TilePose cut));
        Assert.Equal(loop.Client.Presenter.PoseAt(placed).Position, cut.Position);
        Assert.True(loop.Client.TryGetRemoteStepProgress(remote, out float progress));
        Assert.Equal(1f, progress);
        Assert.True(Vector3.Distance(before.Position, cut.Position) > 2f, "the teleport was not a real distance");

        loop.Frames(40);
        loop.Click(new TileCoord(16, 20, 0), TileMoveMode.Walk);
        (float flip, float worst) = loop.ClickWindow(remote, placed);
        Assert.True(flip < 1e-5f, $"the first click after the teleport moved {flip} tiles on its commit frame");
        Assert.True(Math.Abs(worst - ClickRate(4)) < 1e-4f, $"the first click after the teleport moved {worst}");
    }

    /// <summary>
    /// A remote FIRST SEEN part way through a step has no sample before it, so nothing says which door the step
    /// came through, and it is drawn as it always was. The remote stands one tile outside the viewer's interest
    /// and walks in, so the first sample the client ever holds of it is already stepping.
    /// </summary>
    [Fact]
    public void A_remote_first_seen_mid_step_is_not_read_as_a_click()
    {
        var cadence = new TileStepTicks(4, 2);
        using var loop = new RemoteLoop(cadence);
        // The viewer stands on (10, 10) and the default interest radius is 15 tiles.
        long remote = loop.SpawnRemote(new TileCoord(10, 26, 0));
        Assert.False(loop.Client.TryGetRemoteTile(remote, out _), "the remote was in interest before it walked");

        loop.Click(new TileCoord(10, 18, 0), TileMoveMode.Walk);
        int frames = 0;
        float previous;
        while (!loop.Client.TryGetRemoteStepProgress(remote, out previous))
        {
            Assert.True(++frames < 200, "the remote never walked into interest");
            loop.Step();
        }

        // Unflagged, the first sight reads its own tick count over four, a whole quarter. A flagged read would
        // be a third.
        Assert.True(previous < 1f, "the remote was first seen standing, which proves nothing");
        float quarters = previous * 4f;
        Assert.True(Math.Abs(quarters - MathF.Round(quarters)) < 1e-5f,
            $"the first sight read {previous}, which is not a whole number of ticks over the step's total");
        for (int i = 1; i < Window; i++)
        {
            loop.Step();
            Assert.True(loop.Client.TryGetRemoteStepProgress(remote, out float now));
            Assert.True(Math.Abs(now - previous - OrdinaryRate(4)) < 1e-4f,
                $"frame {i} after first sight advanced {now - previous}, not the ordinary {OrdinaryRate(4)}");
            previous = now;
        }
    }

    // A real server and a real client over an in-memory transport, at the issue's tick and frame rate. The
    // client's command tick is phase offset from the server's, the same loopback lesson TileGlideTests carries.
    sealed class RemoteLoop : IDisposable
    {
        public readonly TileWorldServer Server;
        public readonly TileWorldClient Client;
        float serverAccum;
        int seq;

        public RemoteLoop(TileStepTicks cadence)
        {
            var hub = new InMemoryTransportHub();
            TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
            Server = new TileWorldServer(hub.Server,
                TileWorldServerTickTests.Config(new TileCoord(10, 10, 0)) with
                {
                    TickSeconds = Tick,
                    StepTicks = cadence,
                },
                TileMoveSimulatorTests.Bake(doc),
                new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
            Client = new TileWorldClient(hub.CreateClient(), new TileWorldClientConfig
            {
                TickSeconds = Tick,
                StepTicks = cadence,
            }, TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()));
            Client.Tick(0.037f);
            Client.Poll();
            Frames(40);
            Assert.True(Client.IsJoined);
            Assert.True(Client.LocalNetId >= 0, "the client was never seeded");
        }

        // Slot 1, placed before the server's next tick so no serve ever holds it on the spawn tile, then held
        // long enough for the delayed timeline to draw it standing.
        public long SpawnRemote(TileCoord tile)
        {
            long id = Server.SpawnPlayer(slot: 1, "remote", "Rem");
            Server.SetPlayerState(1, TileMoveState.At(tile, TileDirection.N));
            Frames(60);
            return id;
        }

        public void Click(TileCoord goal, TileMoveMode mode) =>
            Server.Enqueue(1, seq++, TileCommand.WalkTo(goal, mode));

        public void Frames(int count)
        {
            for (int i = 0; i < count; i++) Step();
        }

        public void Step()
        {
            Client.Tick(Frame);
            Server.Poll();
            serverAccum += Frame;
            while (serverAccum >= Tick) { serverAccum -= Tick; Server.Tick(Tick); }
            Client.Poll();
            Client.AdvancePresentation(Frame);
        }

        // Steps until the remote's committed tile leaves `from`, then reports how far the body moved on that
        // frame and the worst single frame over the Window frames drawn off the new step's first sample. On every
        // one of those frames the step progress draw priority reads must agree with where the body is drawn.
        public (float Flip, float Worst) ClickWindow(long remote, TileCoord from)
        {
            Assert.True(Client.TryGetRemotePose(remote, out TilePose previous));
            int frames = 0;
            TileCoord tile;
            do
            {
                Assert.True(++frames < 200, "the clicked step never reached the delayed timeline");
                Assert.True(Client.TryGetRemotePose(remote, out previous));
                Step();
                Assert.True(Client.TryGetRemoteTile(remote, out tile));
            }
            while (tile.Equals(from));

            float flip = 0f, worst = 0f;
            for (int i = 0; i < Window; i++)
            {
                if (i > 0) Step();
                Assert.True(Client.TryGetRemotePose(remote, out TilePose now));
                float moved = Vector3.Distance(previous.Position, now.Position);
                if (i == 0) flip = moved;
                worst = Math.Max(worst, moved);
                AssertProgressAgrees(remote, from, tile, now);
                previous = now;
            }
            return (flip, worst);
        }

        // The body's fraction along the step, read back off the pose, against the progress the client reports.
        void AssertProgressAgrees(long remote, TileCoord from, TileCoord to, TilePose pose)
        {
            Assert.True(Client.TryGetRemoteStepProgress(remote, out float progress));
            float drawnX = TileWorldSpace.TileX(pose.Position.X, 1f) - 0.5f;
            float drawnZ = TileWorldSpace.TileZ(pose.Position.Z, 1f) - 0.5f;
            float along = to.X != from.X ? (drawnX - from.X) / (to.X - from.X) : (drawnZ - from.Z) / (to.Z - from.Z);
            Assert.True(Math.Abs(along - progress) < 1e-4f,
                $"draw priority read {progress} while the body was drawn {along} of the way along its step");
        }

        public void Dispose() { Client.Dispose(); Server.Dispose(); }
    }
}
