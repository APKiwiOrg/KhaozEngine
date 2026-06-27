using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Windowing;

// Walkable overworld slice: drive a greybox capsule over the shipped analytic terrain
// (TerrainPresets.Clearing) with a third-person follow camera. WASD move, mouse-drag orbit,
// scroll zoom, shift run, Esc quit. The terrain field is wrapped in TerrainCollision for the
// ground-clamp; the world is STREAMED (TerrainStreamer loads/unloads chunks + their props in a
// ring around the player, so walking any direction streams the world forever) and the capsule is
// static (no walk-cycle yet). Honors KE_MAX_FRAMES so a headless smoke run renders N frames then exits 0.
Console.WriteLine("TerrainWalkSample - WASD move | mouse-drag orbit | scroll zoom | shift run | Esc quit");
using (var app = new TerrainWalkApp())
    app.Run();
return 0;

sealed class TerrainWalkApp : GameApp3D
{
    // Tuning surface (feel-tuned later).
    const float CapsuleRadius = 0.3f;
    const float CapsuleHalfHeight = 0.9f;     // 1.8 m total (height 1.2 + 2*radius 0.6)
    const float PropDrawRadius = 90f;         // distance-cull ring for instanced props around the player

    TerrainField _field = null!;
    TerrainCollision _terrain = null!;
    MeshHandle _capsule;

    // One uploaded mesh per kit id; the streamer scatters per-chunk props from these on demand.
    readonly Dictionary<string, MeshHandle> _propMeshes = new();

    // Streams terrain chunks + props in a ring around the player so the world is effectively endless.
    TerrainStreamer _streamer = null!;
    Scene3DChunkSink _chunkSink = null!;

    CharacterController3D _character = null!;
    FollowCamera3D _camera = null!;
    FollowCameraController _camController = null!;

    public TerrainWalkApp()
        : base(new GameAppOptions
        {
            Title = "KhaozEngine - Terrain walk",
            Width = 1280,
            Height = 720,
            ScaleMode = ScaleMode.Fit,
            ClearColor = new Color(0.45f, 0.62f, 0.85f, 1f),   // sky
        })
    { }

    protected override void OnLoad()
    {
        var sc = Scene;

        // Analytic field + collision wrapper for the ground-clamp.
        _field = new TerrainField(TerrainPresets.Clearing());
        _terrain = new TerrainCollision(_field);

        // 1.8 m greybox capsule (height 1.2 + 2*radius 0.6); mesh bottom sits at y=0 in local space.
        _capsule = sc.LoadMesh(MeshPrimitives.Capsule(radius: CapsuleRadius, height: 1.2f, segments: 16, rings: 6));

        // Character spawns on the ground at the origin.
        _character = new CharacterController3D { CapsuleHalfHeight = CapsuleHalfHeight };
        _character.SetXZ(0f, 0f);
        _character.Update(InputState.Empty, 0f, 0f, _terrain.GroundHeight);   // settle Y onto the ground

        // Follow camera drives rendering via the scene override; the ground delegate keeps the eye above the
        // terrain so it does not sink through the floor when the character is in a dip.
        _camera = new FollowCamera3D { Target = _character.Position, HeightOffset = 1.2f, GroundHeight = _terrain.GroundHeight };
        _camera.Distance = 9f;
        _camController = new FollowCameraController(_camera);
        sc.CameraOverride = _camera;

        // Load the committed CC0 prop kit through the asset pipeline (decompressed glTF -> normalized to its
        // manifest heightMeters with the origin dropped to the base), one uploaded mesh per id.
        string manifestPath = Path.Combine(AppContext.BaseDirectory, "assets", "props", "props.manifest.json");
        AssetManifest manifest = AssetManifest.Load(manifestPath);
        foreach (AssetEntry entry in manifest.Props)
            _propMeshes[entry.Id] = sc.LoadMesh(PropLoader.LoadProp(entry));

        // Endless streamed world: the sink builds chunk meshes + deterministic per-chunk props (same coordinate-hash
        // scatter as before, now over every chunk's area), the streamer keeps a ring of them loaded around the player
        // within a per-frame budget. Prime the first ring before the first frame so the player spawns on solid ground.
        _chunkSink = new Scene3DChunkSink(sc, _field, ScatterConfig.ForestRing(), _propMeshes,
            chunkSize: TerrainChunkRegion.DefaultSize, propDrawRadius: PropDrawRadius);
        _streamer = new TerrainStreamer(StreamerConfig.Default, _chunkSink);
        // Prime the FULL initial ring at load time (this is the loading moment, not a frame, so the per-frame
        // MaxLoadsPerFrame budget is irrelevant here): pump until the loaded set stops growing. From here on
        // OnUpdate amortizes the streaming so a brisk walk never hitches.
        int loadedBefore = -1;
        while (_streamer.Loaded.Count != loadedBefore)
        {
            loadedBefore = _streamer.Loaded.Count;
            _streamer.Update(_character.Position, 0f);
        }
        Console.WriteLine($"Streaming the world: primed {_streamer.Loaded.Count} chunks ({_propMeshes.Count} prop kit meshes).");
    }

    protected override void OnUpdate(float dt)
    {
        if (Input.WasPressed(Key.Escape)) { Quit(); return; }

        _character.Update(Input, dt, _camera.Yaw, _terrain.GroundHeight);

        // Stream the world around the new player position (loads/unloads/re-LODs within MaxLoadsPerFrame).
        _streamer.Update(_character.Position, dt);

        _camera.Target = _character.Position;
        _camera.AspectRatio = FrameHeight > 0 ? (float)FrameWidth / FrameHeight : _camera.AspectRatio;
        _camController.Update(Input, dt);
    }

    protected override void OnDraw3D(Scene3D scene)
    {
        // Streamed terrain + props: the sink draws every loaded chunk mesh and its in-range props.
        _chunkSink.Draw(_character.Position);

        // Draw the capsule so its base sits on the ground (Position is the capsule centre).
        Vector3 p = _character.Position;
        scene.Draw(_capsule, Matrix4x4.CreateTranslation(p.X, p.Y - CapsuleHalfHeight, p.Z), new Color(0.85f, 0.55f, 0.25f, 1f));
    }
}
