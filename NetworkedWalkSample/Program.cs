using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode.LiteNetLib;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Simulation;
using KhaozEngine.Terrain;
using KhaozEngine.Windowing;

// Networked walkable overworld client: connects to a NetworkedWalkServer, drives the local player through a
// WorldClient (predicted + reconciled), and renders a capsule per replicated EntityRenderState over the same
// analytic terrain + deterministic prop scatter as the solo TerrainWalkSample (props are NOT networked). Run
// the server, then two of these clients on localhost to see two players. Usage: NetworkedWalkSample [host] [port].
string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 && int.TryParse(args[1], out int p) ? p : 47700;
// A stable account id so reconnecting (or after a server restart) restores this player's saved position.
// Pass a third arg to use distinct accounts for two clients on one box, e.g. "player1" and "player2".
string account = args.Length > 2 ? args[2] : "player1";

Console.WriteLine($"NetworkedWalkSample -> {host}:{port} as '{account}' | WASD move | mouse-drag orbit | scroll zoom | shift run | Esc quit");
using (var app = new NetworkedWalkApp(host, port, account))
    app.Run();
return 0;

sealed class NetworkedWalkApp : GameApp3D
{
    const int GridRadius = 3;
    const float CapsuleRadius = 0.3f;
    const float CapsuleHalfHeight = 0.9f;
    const float PropDrawRadius = 90f;
    const float TickSeconds = 1f / 30f;

    readonly string _host;
    readonly int _port;
    readonly string _account;

    TerrainField _field = null!;
    TerrainCollision _terrain = null!;
    readonly List<MeshHandle> _chunks = new();
    MeshHandle _capsule;
    readonly Dictionary<string, MeshHandle> _propMeshes = new();
    IReadOnlyList<PropPlacement> _placements = Array.Empty<PropPlacement>();

    FollowCamera3D _camera = null!;
    FollowCameraController _camController = null!;

    WorldClient _client = null!;
    LiteNetLibClientTransport _transport = null!;
    FixedTickHost _clientClock = null!;
    Vector3 _localPos = Vector3.Zero;

    public NetworkedWalkApp(string host, int port, string account)
        : base(new GameAppOptions
        {
            Title = "KhaozEngine - Networked walk",
            Width = 1280,
            Height = 720,
            ScaleMode = ScaleMode.Fit,
            ClearColor = new Color(0.45f, 0.62f, 0.85f, 1f),
        })
    { _host = host; _port = port; _account = account; }

    protected override void OnLoad()
    {
        var sc = Scene;
        _field = new TerrainField(TerrainPresets.Clearing());
        _terrain = new TerrainCollision(_field);

        float size = TerrainChunkRegion.DefaultSize;
        for (int gz = -GridRadius; gz <= GridRadius; gz++)
            for (int gx = -GridRadius; gx <= GridRadius; gx++)
            {
                var region = new TerrainChunkRegion { OriginX = gx * size, OriginZ = gz * size, Size = size };
                var chunk = TerrainChunkBuilder.Build(_field, region, lod: 0);
                _chunks.Add(sc.LoadTerrainChunk(chunk));
            }

        _capsule = sc.LoadMesh(MeshPrimitives.Capsule(radius: CapsuleRadius, height: 1.2f, segments: 16, rings: 6));

        _camera = new FollowCamera3D { Target = Vector3.Zero, HeightOffset = 1.2f, GroundHeight = _terrain.GroundHeight };
        _camera.Distance = 9f;
        _camController = new FollowCameraController(_camera);
        sc.CameraOverride = _camera;

        string manifestPath = Path.Combine(AppContext.BaseDirectory, "assets", "props", "props.manifest.json");
        AssetManifest manifest = AssetManifest.Load(manifestPath);
        foreach (AssetEntry entry in manifest.Props)
            _propMeshes[entry.Id] = sc.LoadMesh(PropLoader.LoadProp(entry));
        _placements = PropScatter.Generate(_field, ScatterConfig.ForestRing(), new RectArea(-58f, -58f, 58f, 16f));

        // Connect: same terrain field on both ends keeps client prediction identical to the server.
        _transport = new LiteNetLibClientTransport(_host, _port);
        _client = new WorldClient(_transport, _terrain.GroundHeight, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = TickSeconds },
            token: System.Text.Encoding.UTF8.GetBytes(_account),
            groundNormal: _terrain.GroundNormal);   // gate prediction identically to the server's authoritative gate
        _clientClock = new FixedTickHost(TickSeconds);
    }

    protected override void OnUpdate(float dt)
    {
        if (Input.WasPressed(Key.Escape)) { Quit(); return; }

        _client.Poll();

        // Drive prediction + input transmit at the fixed tick rate.
        Vector2 move = Vector2.Zero;
        if (Input.IsDown(Key.W)) move.Y += 1f;
        if (Input.IsDown(Key.S)) move.Y -= 1f;
        if (Input.IsDown(Key.D)) move.X += 1f;
        if (Input.IsDown(Key.A)) move.X -= 1f;
        bool run = Input.IsDown(Key.LeftShift) || Input.IsDown(Key.RightShift);
        var cmd = new MoveCommand(move, run, _camera.Yaw);
        _clientClock.Advance(dt, _ => _client.SendInput(cmd));

        _client.AdvancePresentation(dt);

        foreach (EntityRenderState e in _client.Snapshot())
            if (e.IsLocal) _localPos = e.Position;

        _camera.Target = _localPos;
        _camera.AspectRatio = FrameHeight > 0 ? (float)FrameWidth / FrameHeight : _camera.AspectRatio;
        _camController.Update(Input, dt);
    }

    protected override void OnDraw3D(Scene3D scene)
    {
        foreach (var chunk in _chunks)
            scene.DrawTerrainChunk(chunk);

        scene.DrawProps(_placements, _propMeshes, _localPos, PropDrawRadius);

        foreach (EntityRenderState e in _client.Snapshot())
        {
            Vector3 p = e.Position;
            Color tint = e.IsLocal ? new Color(0.85f, 0.55f, 0.25f, 1f) : new Color(0.30f, 0.55f, 0.85f, 1f);
            scene.Draw(_capsule, Matrix4x4.CreateTranslation(p.X, p.Y - CapsuleHalfHeight, p.Z), tint);
        }
    }

    protected override void OnDispose()
    {
        _transport?.Dispose();
        base.OnDispose();
    }
}
