using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Game;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode.LiteNetLib;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Simulation;
using KhaozEngine.Terrain;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>A scripted remote player: its own <see cref="WorldClient"/> connected over loopback, walking a
    /// fixed patrol loop. No camera of its own, so it steers by treating "heading toward the current waypoint" as
    /// a synthetic camera yaw and always sending a pure-forward <see cref="MoveCommand"/> (move = (0,1)) - matching
    /// how <c>CharacterMovement</c> resolves camera-relative move axes into a world direction.</summary>
    sealed class NetBot : IDisposable
    {
        const float WaypointRadius = 1.5f;

        readonly LiteNetLibClientTransport _transport;
        readonly WorldClient _client;
        readonly FixedTickHost _clock;
        readonly Vector2[] _waypoints;
        int _target;

        public NetBot(int port, string token, Vector2[] waypoints, Func<float, float, float> groundHeight,
            Func<float, float, Vector3> groundNormal, float tickSeconds)
        {
            _waypoints = waypoints;
            _transport = new LiteNetLibClientTransport("127.0.0.1", port);
            _client = new WorldClient(_transport, groundHeight, MoveTuning.Default,
                new WorldClientConfig { TickSeconds = tickSeconds },
                token: Encoding.UTF8.GetBytes(token),
                groundNormal: groundNormal);
            _clock = new FixedTickHost(tickSeconds);
        }

        public WorldConnectionState ConnectionState => _client.ConnectionState;

        /// <summary>Pumps the transport a single poll with no elapsed time - used only during connection priming
        /// alongside the server/local-client handshake loop, before the room starts real per-frame stepping.</summary>
        public void PrimePoll() => _client.Poll(0f);

        public void Step(float dt)
        {
            _client.Poll(dt);

            Vector3 pos = _client.LocalRenderState.Position;
            Vector2 target = _waypoints[_target];
            Vector2 toTarget = target - new Vector2(pos.X, pos.Z);
            if (toTarget.LengthSquared() <= WaypointRadius * WaypointRadius)
            {
                _target = (_target + 1) % _waypoints.Length;
                target = _waypoints[_target];
                toTarget = target - new Vector2(pos.X, pos.Z);
            }

            // CharacterMovement resolves MoveCommand.Move as camera-relative (Y = forward, rotated by CameraYaw)
            // into world XZ via forward = (-sin(yaw), 0, -cos(yaw)). A bot with no camera treats "heading to the
            // waypoint" as that synthetic yaw and always sends pure forward (0,1), so it walks straight at the
            // target regardless of the axis convention baked into the forward/right basis.
            Vector2 dir = toTarget.LengthSquared() > 1e-6f ? Vector2.Normalize(toTarget) : Vector2.Zero;
            float yaw = MathF.Atan2(-dir.X, -dir.Y);
            var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: yaw, jump: false);
            _clock.Advance(dt, _ => _client.SendInput(cmd));

            _client.AdvancePresentation(dt);
        }

        public void Dispose()
        {
            _client.Dispose();
            _transport.Dispose();
        }
    }

    /// <summary>Networked walk room: an authoritative WorldServer + a local WorldClient, both stepped on the main
    /// thread over a loopback UDP socket. Demonstrates the predict / replicate / reconcile netcode without
    /// launching a separate server process, ported from NetworkedWalkServer (server construction) and
    /// NetworkedWalkSample (terrain/camera/input/render). Renders through the showcase's shared Scene3D (injected
    /// via Init). Esc returns to the menu. Animated characters are a later task, this room draws capsules. Two
    /// scripted <see cref="NetBot"/> instances patrol the meadow so the local client's Snapshot() replicates
    /// visible remote players.</summary>
    public sealed class RoomNet : GameScene, IGameScene3D
    {
        const int GridRadius = 3;
        const float CapsuleRadius = 0.3f;
        const float CapsuleHalfHeight = 0.9f;
        const float TickSeconds = 1f / 30f;

        // Loopback bind: start at 47750 (distinct from NetworkedWalkServer's default 47700, avoiding a collision
        // if a standalone server sample is also running) and retry a few higher ports so a quick re-enter of the
        // room does not race the previous visit's not-yet-released socket.
        const int BasePort = 47750;
        const int BindAttempts = 5;

        Scene3D _scene = null!;
        Texture2D _white = null!;
        SpriteFont _hud = null!;

        // Guards OnExit/OnUpdate/OnDraw3D against running before OnEnter has built the per-enter state (and
        // OnEnter against leftover state from a previous visit).
        bool _built;

        TerrainField _field = null!;
        TerrainCollision _terrain = null!;
        readonly List<MeshHandle> _chunks = new();
        MeshHandle _capsule;

        int _port;
        LiteNetLibServerTransport _serverTransport = null!;
        WorldServer _server = null!;
        FixedTickHost _serverClock = null!;

        LiteNetLibClientTransport _clientTransport = null!;
        WorldClient _client = null!;
        FixedTickHost _clientClock = null!;
        bool _jumpQueued;   // latches a Space press between fixed ticks so a jump is never dropped on a non-tick frame

        readonly List<NetBot> _bots = new();

        FollowCamera3D _camera = null!;
        FollowCameraController _camController = null!;

        public RoomNet Init(Scene3D scene, Texture2D white, SpriteFont hud)
        {
            _scene = scene; _white = white; _hud = hud;
            return this;
        }

        public override void OnEnter()
        {
            // Same preset NetworkedWalkServer + NetworkedWalkSample both use, so the local client's prediction
            // and the server's authoritative ground agree exactly.
            _field = new TerrainField(TerrainPresets.Clearing());
            _terrain = new TerrainCollision(_field);

            float size = TerrainChunkRegion.DefaultSize;
            for (int gz = -GridRadius; gz <= GridRadius; gz++)
                for (int gx = -GridRadius; gx <= GridRadius; gx++)
                {
                    var region = new TerrainChunkRegion { OriginX = gx * size, OriginZ = gz * size, Size = size };
                    var chunk = TerrainChunkBuilder.Build(_field, region, lod: 0);
                    _chunks.Add(_scene.LoadTerrainChunk(chunk));
                }

            _capsule = _scene.LoadMesh(MeshPrimitives.Capsule(radius: CapsuleRadius, height: 1.2f, segments: 16, rings: 6));

            // Bind the server on a loopback port, retrying on failure. LiteNetLibServerTransport's ctor throws
            // InvalidOperationException when NetManager.Start(port) fails (e.g. the previous visit's socket has
            // not fully released yet), so a re-entered room does not race a stale bind.
            _port = BasePort;
            for (int attempt = 0; attempt < BindAttempts; attempt++)
            {
                int candidate = BasePort + attempt;
                try
                {
                    _serverTransport = new LiteNetLibServerTransport(candidate);
                    _port = candidate;
                    break;
                }
                catch (InvalidOperationException) when (attempt < BindAttempts - 1)
                {
                    // Try the next port.
                }
            }

            var serverConfig = new WorldServerConfig
            {
                TickSeconds = TickSeconds,
                MaxPlayers = 8,
                SpawnPosition = slot => new Vector3(48f + slot * 4f, 0f, 24f),
            };
            _server = new WorldServer(_serverTransport, serverConfig, _terrain.GroundHeight, MoveTuning.Default,
                groundNormal: _terrain.GroundNormal);
            _serverClock = new FixedTickHost(TickSeconds);

            _clientTransport = new LiteNetLibClientTransport("127.0.0.1", _port);
            _client = new WorldClient(_clientTransport, _terrain.GroundHeight, MoveTuning.Default,
                new WorldClientConfig { TickSeconds = TickSeconds },
                token: Encoding.UTF8.GetBytes("player"),
                groundNormal: _terrain.GroundNormal);
            _clientClock = new FixedTickHost(TickSeconds);

            // Two scripted remote players, patrolling loops in the flat meadow south of the biome blend (x in
            // [44,70], z in [15,40] - clear of the lake near x=-13 and of the mountain band starting past z=48),
            // so replication is visible without the bots wandering into rough terrain.
            _bots.Add(new NetBot(_port, "bot1",
                new[] { new Vector2(48f, 20f), new Vector2(66f, 18f), new Vector2(68f, 36f), new Vector2(46f, 34f) },
                _terrain.GroundHeight, _terrain.GroundNormal, TickSeconds));
            _bots.Add(new NetBot(_port, "bot2",
                new[] { new Vector2(62f, 34f), new Vector2(44f, 32f), new Vector2(46f, 16f), new Vector2(64f, 18f) },
                _terrain.GroundHeight, _terrain.GroundNormal, TickSeconds));

            _camera = new FollowCamera3D { Target = Vector3.Zero, HeightOffset = 1.2f, GroundHeight = _terrain.GroundHeight };
            _camera.Distance = 9f;
            _camController = new FollowCameraController(_camera);
            _scene.CameraOverride = _camera;

            // Prime the loopback connection: pump both ends a few ticks so the handshake completes before the
            // first rendered frame (otherwise the first several frames would show the pre-join empty snapshot).
            // LiteNetLib's connect handshake is a real (if loopback-fast) UDP round-trip, so a short real sleep
            // between polls is needed alongside the polling - a tight zero-delay loop can spin without ever
            // giving the socket time to complete the handshake. Bots are primed in the same loop so all 3 clients
            // are connected before the first rendered frame.
            for (int i = 0; i < 60 && _client.ConnectionState != WorldConnectionState.Connected; i++)
            {
                _server.Poll();
                _server.Tick(TickSeconds);
                _client.Poll(TickSeconds);
                foreach (NetBot b in _bots) b.PrimePoll();
                System.Threading.Thread.Sleep(5);
            }

            _built = true;
        }

        public override void OnUpdate(float dt)
        {
            if (Manager!.Input.WasPressed(Key.Escape)) { Manager!.Pop(); return; }
            if (!_built) return;

            // Server tick (authoritative).
            _server.Poll();
            _serverClock.Advance(dt, _ => _server.Tick(TickSeconds));

            // Local client: input -> predict -> transmit, then presentation.
            _client.Poll(dt);

            var m = Manager!;
            Vector2 move = Vector2.Zero;
            if (m.Input.IsDown(Key.W)) move.Y += 1f;
            if (m.Input.IsDown(Key.S)) move.Y -= 1f;
            if (m.Input.IsDown(Key.D)) move.X += 1f;
            if (m.Input.IsDown(Key.A)) move.X -= 1f;
            bool run = m.Input.IsDown(Key.LeftShift) || m.Input.IsDown(Key.RightShift);
            if (m.Input.WasPressed(Key.Space)) _jumpQueued = true;   // latch, consumed on the next tick so it never misses
            bool jump = _jumpQueued;
            _jumpQueued = false;
            var cmd = new MoveCommand(move, run, _camera.Yaw, jump);
            _clientClock.Advance(dt, _ => _client.SendInput(cmd));

            _client.AdvancePresentation(dt);

            foreach (NetBot b in _bots) b.Step(dt);

            _camera.Target = _client.LocalRenderState.Position;
            _camera.AspectRatio = m.FrameHeight > 0 ? (float)m.FrameWidth / m.FrameHeight : _camera.AspectRatio;
            _camController.Update(m.Input, dt);
        }

        public void OnDraw3D(Scene3D scene)
        {
            if (!_built) return;

            foreach (var chunk in _chunks)
                scene.DrawTerrainChunk(chunk);

            foreach (EntityRenderState e in _client.Snapshot())
            {
                Vector3 p = e.Position;
                Color tint = e.IsLocal ? new Color(0.85f, 0.55f, 0.25f, 1f) : new Color(0.30f, 0.55f, 0.85f, 1f);
                scene.Draw(_capsule, Matrix4x4.CreateTranslation(p.X, p.Y - CapsuleHalfHeight, p.Z), tint);
            }
        }

        public override void OnDraw2D(SpriteBatch batch) { /* Task 5: net HUD */ }

        public override void OnExit()
        {
            if (!_built) return;
            _built = false;

            foreach (NetBot b in _bots) b.Dispose();
            _bots.Clear();

            _client.Dispose();
            _clientTransport.Dispose();
            _serverTransport.Dispose();

            _scene.UnloadMesh(_capsule);
            foreach (MeshHandle h in _chunks) _scene.UnloadMesh(h);
            _chunks.Clear();

            _scene.CameraOverride = null;

            _field = null!;
            _terrain = null!;
            _server = null!;
            _serverClock = null!;
            _client = null!;
            _clientClock = null!;
            _camera = null!;
            _camController = null!;
        }
    }
}
