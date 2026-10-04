using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The server tick on the client: <see cref="WorldClient.LatestServerTick"/> follows each ticked ingest, and
/// <see cref="WorldClient.RemoteRenderTick"/> names the fractional server tick the remotes are drawn at, bracketed
/// at the remote render time by the rule the remote samples follow. Off by default and on the wire only with
/// <see cref="WorldClientConfig.ReceiveServerTick"/>.
/// </summary>
public class WorldClientServerTickTests
{
    private const float Dt = 1f / 30f;
    private static readonly Func<float, float, float> Flat = (_, _) => 0f;
    private static readonly MoveCommand Right = new(new Vector2(1f, 0f), run: false, cameraYaw: 0f);   // +X

    [Fact]
    public void DefaultOffSendsNoHelloAndReadsUnknown()
    {
        using Rig rig = Rig.Create(receiveTick: false);

        for (int i = 0; i < 10; i++) rig.Frame(1, Dt);

        Assert.NotEmpty(rig.KindsToA);
        Assert.All(rig.KindsToA, k => Assert.True(k is MoveProtocol.ServerFrameKind.Snapshot or MoveProtocol.ServerFrameKind.Delta, $"{k}"));
        Assert.False(rig.ASentTheHello);
        Assert.Equal(-1L, rig.A.LatestServerTick);
        Assert.Equal(-1.0, rig.A.RemoteRenderTick);
    }

    [Fact]
    public void LatestServerTickFollowsEachIngest()
    {
        using Rig rig = Rig.Create();

        Assert.True(rig.ASentTheHello);
        Assert.Contains(MoveProtocol.ServerFrameKind.TickedDelta, rig.KindsToA);
        for (int i = 0; i < 10; i++)
        {
            rig.Serve(1);
            rig.A.Poll();
            Assert.Equal(rig.Server.ServerTick, rig.A.LatestServerTick);
        }
    }

    [Fact]
    public void SteadyRenderTickTrailsByTheDelay()
    {
        using Rig rig = Rig.Create();

        // The render time is the presentation clock less InterpolationDelayTicks (2) ticks. A frame polls, stamping
        // its ingest at the clock, then presents, advancing the clock one tick past that stamp. So in steady state the
        // remotes are drawn one tick behind the newest ingest: delay minus the frame's own advance.
        const double delayTicks = 2;
        for (int i = 0; i < 10; i++) rig.Frame(1, Dt);
        for (int i = 0; i < 30; i++)
        {
            rig.Frame(1, Dt);
            Assert.Equal(rig.A.LatestServerTick - delayTicks + 1, rig.A.RemoteRenderTick, 1e-9);
        }
    }

    [Fact]
    public void TheRenderTickNamesTheTickTheRemoteIsDrawnAt()
    {
        using Rig rig = Rig.Create();
        var bX = new Dictionary<long, float>();
        rig.Server.OnAfterTick += _ =>
        {
            Assert.True(rig.Server.TryGetPlayerState(rig.BSlot, out PlayerMoveState b));
            bX[rig.Server.ServerTick] = b.Position.X;
        };

        // 1/45 s per frame against a 1/30 s tick: a non-integer ratio, so the render time lands between ingests.
        double served = 0;
        int compared = 0;
        for (int frame = 0; frame < 60; frame++)
        {
            served += 1.0 / 45.0;
            int ticks = 0;
            while (served >= Dt * (ticks + 1)) ticks++;
            served -= Dt * ticks;
            rig.Frame(ticks, 1f / 45f);

            double renderTick = rig.A.RemoteRenderTick;
            if (frame < 10 || !bX.ContainsKey((long)Math.Floor(renderTick))) continue;
            long lo = (long)Math.Floor(renderTick);
            float expected = bX.TryGetValue(lo + 1, out float hi)
                ? bX[lo] + (hi - bX[lo]) * (float)(renderTick - lo)
                : bX[lo];
            Assert.Equal(expected, rig.RemoteX(), 1e-4f);
            compared++;
        }
        Assert.True(compared >= 45, $"only {compared} frames compared");
        Assert.True(bX[rig.Server.ServerTick] > bX[rig.Server.ServerTick - 30] + 0.5f, "B should be walking");
    }

    [Fact]
    public void CollapsedIngestsKeepTheNewestTickAndStayMonotonic()
    {
        using Rig rig = Rig.Create();
        int[] ticksPerFrame = { 0, 2, 3 };
        float[] dts = { 1f / 60f, 1f / 20f, 1f / 30f };
        double clock = rig.PresentationClock;
        var stamps = new List<(double stamp, long tick)>();
        double previous = rig.A.RemoteRenderTick;

        for (int frame = 0; frame < 45; frame++)
        {
            int ticks = ticksPerFrame[frame % 3];
            long before = rig.Server.ServerTick;
            rig.Serve(ticks);
            rig.A.Poll();
            if (ticks > 0)
            {
                // Every tick of the frame arrived in this one Poll, so all of them share its stamp.
                Assert.Equal(before + ticks, rig.A.LatestServerTick);
                stamps.Add((clock, rig.A.LatestServerTick));
            }
            float dt = dts[frame % 3];
            rig.A.AdvancePresentation(dt);
            clock += dt;

            double renderTick = rig.A.RemoteRenderTick;
            Assert.True(renderTick >= previous, $"frame {frame}: {renderTick} after {previous}");
            previous = renderTick;
            // The reference holds this loop's ingests only, so it answers once the render time reaches the first.
            double renderTime = clock - 2 * Dt;
            if (stamps.Count > 0 && renderTime >= stamps[0].stamp)
                Assert.Equal(Bracket(stamps, renderTime), renderTick, 1e-9);
        }
        Assert.True(stamps.Count >= 20 && previous > stamps[5].tick, "the reference never covered the render time");
    }

    [Fact]
    public void StarvationHoldsTheNewestTick()
    {
        using Rig rig = Rig.Create();
        for (int i = 0; i < 10; i++) rig.Frame(1, Dt);
        long latest = rig.A.LatestServerTick;
        double previous = rig.A.RemoteRenderTick;
        Assert.True(previous < latest);

        for (int i = 0; i < 10; i++)
        {
            rig.A.Poll();
            rig.A.AdvancePresentation(Dt);
            Assert.True(rig.A.RemoteRenderTick >= previous);
            previous = rig.A.RemoteRenderTick;
        }

        Assert.Equal(latest, rig.A.LatestServerTick);
        Assert.Equal((double)latest, rig.A.RemoteRenderTick);
    }

    [Fact]
    public void WithoutRemoteInterpolationTheRenderTickIsTheLatest()
    {
        using Rig rig = Rig.Create(interpolate: false);

        for (int i = 0; i < 10; i++)
        {
            rig.Frame(1 + i % 2, Dt);
            Assert.Equal(rig.Server.ServerTick, rig.A.LatestServerTick);
            Assert.Equal((double)rig.A.LatestServerTick, rig.A.RemoteRenderTick);
        }
    }

    [Fact]
    public void PlainFramesLeaveTheTickUnknown()
    {
        using var hub = new InMemoryTransportHub();
        var server = new NetServer(hub.Server, maxPlayers: 4, new AllowAllAuthenticator());
        using var a = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = Dt, ReceiveServerTick = true });
        int slot = -1;
        for (int i = 0; i < 20 && slot < 0; i++)
        {
            server.Poll();
            while (server.TryDequeueEvent(out ServerSessionEvent ev))
                if (ev.Kind == ServerSessionEventKind.Joined) slot = ev.Slot;
            a.Poll();
        }
        Assert.True(slot >= 0);
        Assert.True(a.Joined);

        byte[] emptySnapshot = new byte[4];   // entity count 0
        for (int i = 0; i < 10; i++)
        {
            // An older server answers the hello with nothing and keeps serving plain frames.
            server.SendTo(slot, MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Snapshot,
                MoveProtocol.EncodeSnapshotFrame(localNetId: 42, ackSeq: -1, emptySnapshot)), NetChannelReliability.ReliableOrdered);
            server.Poll();
            a.Poll();
            a.AdvancePresentation(Dt);

            Assert.Equal(42L, a.LocalNetId);
            Assert.Equal(-1L, a.LatestServerTick);
            Assert.Equal(-1.0, a.RemoteRenderTick);
        }
    }

    [Fact]
    public void ANewAttemptForgetsTheTick()
    {
        using Rig rig = Rig.Create(reconnect: true);
        for (int i = 0; i < 5; i++) rig.Frame(1, Dt);
        Assert.True(rig.A.LatestServerTick > 0);
        Assert.True(rig.A.RemoteRenderTick > 0);

        rig.Hub.DisconnectClient(rig.ATransport);
        bool sawUnknown = false;
        bool resumed = false;
        for (int i = 0; i < 200 && !resumed; i++)
        {
            int attempts = rig.AAttempts;
            rig.Serve(1);
            rig.A.Poll(Dt);
            rig.A.AdvancePresentation(Dt);
            if (rig.AAttempts > attempts)
            {
                // This Poll built the new attempt: nothing of the old session's tick survives it.
                Assert.Equal(-1L, rig.A.LatestServerTick);
                Assert.Equal(-1.0, rig.A.RemoteRenderTick);
                sawUnknown = true;
            }
            resumed = sawUnknown && rig.A.LatestServerTick >= 0;
        }

        Assert.True(sawUnknown, "the client never started a new attempt");
        Assert.True(resumed, "ticks never resumed");
        Assert.Equal(rig.Server.ServerTick, rig.A.LatestServerTick);
        rig.Serve(1);
        rig.A.Poll(Dt);
        Assert.Equal(rig.Server.ServerTick, rig.A.LatestServerTick);
    }

    [Fact]
    public void UnreliableDeltasWithTheTickAreRefused()
    {
        using var hub = new InMemoryTransportHub();
        ArgumentException thrown = Assert.Throws<ArgumentException>(() => new WorldClient(hub.CreateClient(), Flat,
            MoveTuning.Default, new WorldClientConfig { ReceiveServerTick = true, RequestUnreliableDeltaReplication = true }));

        Assert.Equal("config", thrown.ParamName);
        Assert.Contains(nameof(WorldClientConfig.ReceiveServerTick), thrown.Message);
        Assert.Contains(nameof(WorldClientConfig.RequestUnreliableDeltaReplication), thrown.Message);
    }

    // The reference bracket over (stamp, newest tick) pairs: the oldest before the first, the newest at or past the
    // last, else the lerp by the stamps.
    private static double Bracket(List<(double stamp, long tick)> stamps, double renderTime)
    {
        if (stamps.Count == 0) return -1;
        if (renderTime < stamps[0].stamp) return stamps[0].tick;
        for (int i = stamps.Count - 1; i >= 0; i--)
        {
            if (stamps[i].stamp > renderTime) continue;
            if (i == stamps.Count - 1) return stamps[i].tick;
            (double t0, long k0) = stamps[i];
            (double t1, long k1) = stamps[i + 1];
            return k0 + (k1 - k0) * ((renderTime - t0) / (t1 - t0));
        }
        return stamps[0].tick;
    }

    /// <summary>A sharded server with observer A (opted in unless told otherwise) and remote B walking +X.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly WireRecorder aWire;
        private int bSlot = -1;

        private Rig(InMemoryTransportHub hub, ShardedWorldServer server, WireRecorder aWire, WorldClient a, WorldClient b)
        {
            Hub = hub;
            Server = server;
            this.aWire = aWire;
            A = a;
            B = b;
        }

        public InMemoryTransportHub Hub { get; }
        public ShardedWorldServer Server { get; }
        public WorldClient A { get; }
        public WorldClient B { get; }
        public INetTransport ATransport => aWire.Current!;
        public int AAttempts => aWire.Attempts;
        public List<MoveProtocol.ServerFrameKind> KindsToA => aWire.Kinds;
        public bool ASentTheHello => aWire.SentTickHello;
        public double PresentationClock { get; private set; }

        public int BSlot
        {
            get
            {
                if (bSlot >= 0) return bSlot;
                for (int slot = 0; slot < 8; slot++)
                    if (Server.TryGetPlayerNetId(slot, out long netId) && netId == B.LocalNetId) return bSlot = slot;
                throw new Xunit.Sdk.XunitException("B has no slot");
            }
        }

        public static Rig Create(bool receiveTick = true, bool interpolate = true, bool reconnect = false)
        {
            var hub = new InMemoryTransportHub();
            // The default 24 m interest radius (a sharded server caps it at OverlapMargin) holds B for every walk here.
            var server = new ShardedWorldServer(hub.Server,
                new ShardedWorldServerConfig { TickSeconds = Dt, MaxPlayers = 8 }, Flat, MoveTuning.Default);
            var aWire = new WireRecorder(hub);
            var aConfig = new WorldClientConfig
            {
                TickSeconds = Dt,
                InterpolateRemotes = interpolate,
                ReceiveServerTick = receiveTick,
                Reconnect = new ReconnectBackoff { InitialSeconds = 0.1f, Multiplier = 2f, MaxSeconds = 0.2f },
            };
            WorldClient a = reconnect
                ? new WorldClient(aWire.Connect, Flat, MoveTuning.Default, aConfig)
                : new WorldClient(aWire.Connect(), Flat, MoveTuning.Default, aConfig);
            var b = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = Dt });
            var rig = new Rig(hub, server, aWire, a, b);
            for (int i = 0; i < 30; i++) rig.Frame(1, Dt);
            Assert.True(a.Joined && b.Joined, "both clients should be joined after warm-up");
            Assert.Contains(a.Snapshot(), e => !e.IsLocal && e.Id.Value == b.LocalNetId);
            return rig;
        }

        /// <summary>Runs <paramref name="ticks"/> server ticks with B walking, delivering each to B at once.</summary>
        public void Serve(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                B.SendInput(Right);
                Server.Poll();
                Server.Tick(Dt);
                B.Poll();
            }
        }

        /// <summary>One presentation frame of A: <paramref name="ticks"/> server ticks, one Poll, one present.</summary>
        public void Frame(int ticks, float presentDt)
        {
            Serve(ticks);
            A.Poll();
            A.AdvancePresentation(presentDt);
            PresentationClock += presentDt;
        }

        public float RemoteX()
        {
            foreach (EntityRenderState e in A.Snapshot())
                if (!e.IsLocal && e.Id.Value == B.LocalNetId) return e.Position.X;
            throw new Xunit.Sdk.XunitException("B is not visible to A");
        }

        public void Dispose()
        {
            A.Dispose();
            B.Dispose();
            Hub.Dispose();
        }
    }

    /// <summary>Builds A's transports and records the replication frame kinds it receives and whether it sent the
    /// tick hello, read off the session layer.</summary>
    private sealed class WireRecorder
    {
        private static readonly byte[] TickHello = MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.ServerTickCapable);
        private readonly InMemoryTransportHub hub;

        public WireRecorder(InMemoryTransportHub hub) => this.hub = hub;

        public INetTransport? Current { get; private set; }
        public int Attempts { get; private set; }
        public List<MoveProtocol.ServerFrameKind> Kinds { get; } = new();
        public bool SentTickHello { get; private set; }

        public INetTransport Connect()
        {
            Attempts++;
            INetTransport inner = hub.CreateClient();
            Current = inner;
            return new Tap(inner, this);
        }

        private sealed class Tap : INetTransport
        {
            private readonly INetTransport inner;
            private readonly WireRecorder recorder;

            public Tap(INetTransport inner, WireRecorder recorder)
            {
                this.inner = inner;
                this.recorder = recorder;
            }

            public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
            {
                if (SessionFrame.ReadOpcode(payload) == SessionOpcode.Data
                    && SessionFrame.ReadBody(payload).AsSpan().SequenceEqual(TickHello))
                    recorder.SentTickHello = true;
                inner.Send(target, payload, reliability);
            }

            public bool TryDequeueEvent(out NetEvent ev)
            {
                if (!inner.TryDequeueEvent(out ev)) return false;
                if (ev.Type == NetEventType.Data && SessionFrame.ReadOpcode(ev.Data) == SessionOpcode.Data
                    && MoveProtocol.TryDecodeServerFrame(SessionFrame.ReadBody(ev.Data), out MoveProtocol.ServerFrameKind kind, out _)
                    && kind is not MoveProtocol.ServerFrameKind.Notice and not MoveProtocol.ServerFrameKind.GameMessage)
                    recorder.Kinds.Add(kind);
                return true;
            }

            public void Poll() => inner.Poll();
            public void Disconnect(NetConnectionId connection) => inner.Disconnect(connection);
            public void Dispose() => inner.Dispose();
        }
    }
}
