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
// ground-clamp; nothing here is streamed (fixed chunk grid) and the capsule is static (no
// walk-cycle yet). Honors KE_MAX_FRAMES so a headless smoke run renders N frames then exits 0.
Console.WriteLine("TerrainWalkSample - WASD move | mouse-drag orbit | scroll zoom | shift run | Esc quit");
using (var app = new TerrainWalkApp())
    app.Run();
return 0;

sealed class TerrainWalkApp : GameApp3D
{
    // Tuning surface (feel-tuned later).
    const int GridRadius = 3;                 // 7x7 chunks (2*radius+1)
    const float CapsuleRadius = 0.3f;
    const float CapsuleHalfHeight = 0.9f;     // 1.8 m total (height 1.2 + 2*radius 0.6)
    const float PropDrawRadius = 90f;         // distance-cull ring for instanced props around the player

    TerrainField _field = null!;
    TerrainCollision _terrain = null!;
    readonly List<MeshHandle> _chunks = new();
    MeshHandle _capsule;

    // One uploaded mesh per kit id + the deterministic scatter placements (queued each frame, distance-culled).
    readonly Dictionary<string, MeshHandle> _propMeshes = new();
    IReadOnlyList<PropPlacement> _placements = Array.Empty<PropPlacement>();

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

        // Fixed NxN grid of chunks around the origin, meshed at the densest LOD (no streaming here).
        float size = TerrainChunkRegion.DefaultSize;
        for (int gz = -GridRadius; gz <= GridRadius; gz++)
            for (int gx = -GridRadius; gx <= GridRadius; gx++)
            {
                var region = new TerrainChunkRegion { OriginX = gx * size, OriginZ = gz * size, Size = size };
                var chunk = TerrainChunkBuilder.Build(_field, region, lod: 0);
                _chunks.Add(sc.LoadTerrainChunk(chunk));
            }

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

        // Deterministic coordinate-hash forest ring around the clearing (parity with the greybox scatter).
        _placements = PropScatter.Generate(_field, ScatterConfig.ForestRing(), new RectArea(-58f, -58f, 58f, 16f));
        Console.WriteLine($"Scattered {_placements.Count} props across the clearing ({_propMeshes.Count} kit meshes).");
    }

    protected override void OnUpdate(float dt)
    {
        if (Input.WasPressed(Key.Escape)) { Quit(); return; }

        _character.Update(Input, dt, _camera.Yaw, _terrain.GroundHeight);

        _camera.Target = _character.Position;
        _camera.AspectRatio = FrameHeight > 0 ? (float)FrameWidth / FrameHeight : _camera.AspectRatio;
        _camController.Update(Input, dt);
    }

    protected override void OnDraw3D(Scene3D scene)
    {
        foreach (var chunk in _chunks)
            scene.DrawTerrainChunk(chunk);

        // Instanced forest within the draw radius of the player (distance-culled; batches per kit mesh).
        scene.DrawProps(_placements, _propMeshes, _character.Position, PropDrawRadius);

        // Draw the capsule so its base sits on the ground (Position is the capsule centre).
        Vector3 p = _character.Position;
        scene.Draw(_capsule, Matrix4x4.CreateTranslation(p.X, p.Y - CapsuleHalfHeight, p.Z), new Color(0.85f, 0.55f, 0.25f, 1f));
    }
}
