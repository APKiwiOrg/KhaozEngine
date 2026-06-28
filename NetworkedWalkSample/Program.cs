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
// WorldClient (predicted + reconciled), and renders an animated character per replicated EntityRenderState over
// the same analytic terrain + deterministic prop scatter as the solo TerrainWalkSample (props are NOT networked).
// The per-player avatars are driven by ReplicatedCharacterAnimators: WorldClient.Snapshot() is mapped to one
// CharacterSample per entity each frame (the LOCAL player carries its exact grounded + vertical velocity from the
// new WorldClient accessors; remotes are position-only and the bridge derives speed / air state / facing from the
// position delta). Falls back to a capsule per entity if the character asset fails to load. Run the server, then
// two of these clients on localhost to see two animated players. Usage: NetworkedWalkSample [host] [port] [account].
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

    // Per-player animated avatars driven by the position stream the netcode surfaces (ReplicatedCharacterAnimators):
    // one AnimatedCharacter per replicated entity, lifecycle-managed. Capsule fallback if the asset fails to load.
    SkinnedMeshHandle _characterMesh;
    ReplicatedCharacterAnimators? _animators;
    bool _animated;
    readonly List<CharacterSample> _samples = new();

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

        // The animated-avatar bridge: one skinned mesh shared by every player, one AnimatedCharacter brain per
        // replicated entity. Capsule fallback if the asset is missing/unreadable.
        TryLoadAnimators(sc);

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

        // Map the replicated render states to engine-neutral samples and advance the avatar bridge once per frame.
        // The local player carries its exact movement (so its jump/fall read true, not finite-differenced); remotes
        // are position-only and the bridge derives speed / air state / facing from the position delta. Feet position
        // (centre minus the capsule half-height) so the model's feet, not its centre, sit on the ground.
        _samples.Clear();
        foreach (EntityRenderState e in _client.Snapshot())
        {
            if (e.IsLocal) _localPos = e.Position;
            var feet = new Vector3(e.Position.X, e.Position.Y - CapsuleHalfHeight, e.Position.Z);
            _samples.Add(e.IsLocal
                ? new CharacterSample(e.Id.Value, feet, isLocal: true, _client.LocalGrounded, _client.LocalVerticalVelocity)
                : new CharacterSample(e.Id.Value, feet));
        }
        _animators?.Update(_samples, dt);

        _camera.Target = _localPos;
        _camera.AspectRatio = FrameHeight > 0 ? (float)FrameWidth / FrameHeight : _camera.AspectRatio;
        _camController.Update(Input, dt);
    }

    protected override void OnDraw3D(Scene3D scene)
    {
        foreach (var chunk in _chunks)
            scene.DrawTerrainChunk(chunk);

        scene.DrawProps(_placements, _propMeshes, _localPos, PropDrawRadius);

        if (_animated && _animators is not null)
        {
            // Draw the bridge's live avatars: World already places + faces + scales each (scale baked via the tuning).
            foreach (CharacterPose pose in _animators.Live)
            {
                Color tint = pose.IsLocal ? new Color(0.85f, 0.55f, 0.25f, 1f) : new Color(0.30f, 0.55f, 0.85f, 1f);
                scene.DrawSkinned(_characterMesh, pose.Pose, pose.World, tint);
            }
        }
        else
        {
            foreach (EntityRenderState e in _client.Snapshot())
            {
                Vector3 p = e.Position;
                Color tint = e.IsLocal ? new Color(0.85f, 0.55f, 0.25f, 1f) : new Color(0.30f, 0.55f, 0.85f, 1f);
                scene.Draw(_capsule, Matrix4x4.CreateTranslation(p.X, p.Y - CapsuleHalfHeight, p.Z), tint);
            }
        }
    }

    // Skinned-ingest the committed Quaternius Universal CC0 character + its clips, then build a
    // ReplicatedCharacterAnimators (one AnimatedCharacter brain per replicated entity). On any failure the sample
    // keeps the per-entity capsule.
    void TryLoadAnimators(Scene3D sc)
    {
        try
        {
            string charPath = Path.Combine(AppContext.BaseDirectory, "assets", "character", "Player.glb");
            (SkinnedGltfMesh charMesh, GltfMaterialMaps charMaps) = GltfLoader.LoadSkinnedWithMaterial(charPath);
            if (charMesh.Skeleton is null) { Console.WriteLine("Character has no skeleton; using capsules."); return; }
            _characterMesh = sc.LoadSkinnedMesh(charMesh, charMaps);

            var byName = new Dictionary<string, AnimationClip>();
            foreach (AnimationClip c in GltfLoader.LoadAnimations(charPath)) byName[c.Name] = c;
            var clips = new Dictionary<LocomotionState, AnimationClip>();
            void Map(LocomotionState st, string name) { if (byName.TryGetValue(name, out AnimationClip? c)) clips[st] = c; }
            Map(LocomotionState.Idle, "Idle");
            Map(LocomotionState.Walk, "Walk");
            Map(LocomotionState.Run, "Run");
            Map(LocomotionState.Jump, "Jump");
            Map(LocomotionState.Fall, "Fall");
            if (clips.Count == 0) { Console.WriteLine("Character has no expected clips; using capsules."); return; }

            // Auto-fit the model to the 1.8 m capsule height (asset-agnostic) and bake that scale into the bridge tuning.
            float modelHeight = ModelHeight(charMesh);
            float scale = modelHeight > 0.01f ? (CapsuleHalfHeight * 2f) / modelHeight : 1f;
            CharacterAnimatorTuning tuning = CharacterAnimatorTuning.Default;
            tuning.Scale = scale;
            tuning.Locomotion = new LocomotionThresholds(0.1f, 4.5f);   // split walk/run at the server's 3/6 m/s feel

            // The convenience ctor builds one brain per entity off the shared skeleton + clips, applying the tuning.
            _animators = new ReplicatedCharacterAnimators(charMesh.Skeleton, clips, tuning);
            _animated = true;
            Console.WriteLine($"Animated avatars: {charMesh.BoneCount} bones, states [{string.Join(", ", clips.Keys)}], scale {scale:0.00}.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Character load failed ({e.Message}); falling back to capsules.");
            _animated = false;
        }
    }

    // Model-space height (max - min Y) of the rest mesh, for the capsule-match scale.
    static float ModelHeight(SkinnedGltfMesh mesh)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (SkinnedVertex v in mesh.Vertices) { min = MathF.Min(min, v.Position.Y); max = MathF.Max(max, v.Position.Y); }
        return max - min;
    }

    protected override void OnDispose()
    {
        _transport?.Dispose();
        base.OnDispose();
    }
}
