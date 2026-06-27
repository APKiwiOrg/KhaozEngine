using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Collision;
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
bool bounded = Array.Exists(args, a => a is "bounded" or "--bounded");
Console.WriteLine(bounded
    ? "Bounded clearing - mountains ring the play area; ONE pass to the NORTH (+Z) is the way out. You can't climb the walls. WASD move | space jump | mouse-drag orbit | scroll zoom | shift run | Esc quit"
    : "TerrainWalkSample - WASD move | space jump (run off a cliff and fall) | mouse-drag orbit | scroll zoom | shift run | Esc quit");
using (var app = new TerrainWalkApp(bounded))
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

    // Static-world collision: the nearby scattered props (+ a hand-placed inn) are solid.
    WorldColliders _colliders = null!;

    // Bounded mode: enclose the clearing in a RimFeature mountain wall and wire the slope gate so the rim
    // can't be climbed (the player is held inside; the +Z pass is the one way out).
    readonly bool _bounded;

    public TerrainWalkApp(bool bounded = false)
        : base(new GameAppOptions
        {
            Title = bounded ? "KhaozEngine - Bounded clearing" : "KhaozEngine - Terrain walk",
            Width = 1280,
            Height = 720,
            ScaleMode = ScaleMode.Fit,
            ClearColor = new Color(0.45f, 0.62f, 0.85f, 1f),   // sky
        })
    { _bounded = bounded; }

    protected override void OnLoad()
    {
        var sc = Scene;

        // --- rendering consult (outline flicker) ---------------------------------------------
        // The depth/normal edge outline was built for the orthographic IsoCamera3D (linear z/w); this sample
        // is the first to drive it with a PERSPECTIVE FollowCamera3D, where z/w is non-linear, so a fixed depth
        // threshold pops edges in/out on zoom, and the fixed 1600x900 internal target under-samples the dense
        // foliage on HiDPI (shimmer on rotate). MatchViewport renders the outline at native window res - the
        // free game-side half of the fix. Live keys below A/B the rest.
        sc.Post.RenderScale = RenderScale.MatchViewport;
        Console.WriteLine("Render debug: [M] RenderScale Fixed<->MatchViewport | [O] outline on/off | " +
                          "[K]/[L] depth-threshold -/+ | [G]/[H] normal-threshold -/+");

        // Analytic field + collision wrapper for the ground-clamp. Bounded mode rings the clearing in mountains.
        _field = new TerrainField(_bounded ? TerrainPresets.BoundedClearing() : TerrainPresets.Clearing());
        _terrain = new TerrainCollision(_field);

        // 1.8 m greybox capsule (height 1.2 + 2*radius 0.6); mesh bottom sits at y=0 in local space.
        _capsule = sc.LoadMesh(MeshPrimitives.Capsule(radius: CapsuleRadius, height: 1.2f, segments: 16, rings: 6));

        // Character spawns on the ground at the origin. CapsuleRadius matches the visible greybox capsule so the
        // collision footprint lines up with what's drawn.
        _character = new CharacterController3D { CapsuleHalfHeight = CapsuleHalfHeight, CapsuleRadius = CapsuleRadius };
        _character.SetXZ(0f, 0f);
        _character.Update(InputState.Empty, 0f, 0f, _terrain.GroundHeight, _terrain.GroundNormal);   // settle Y onto the ground (slope gate wired so cliffs aren't climbable)

        // Follow camera drives rendering via the scene override; the ground delegate keeps the eye above the
        // terrain so it does not sink through the floor when the character is in a dip.
        _camera = new FollowCamera3D { Target = _character.Position, HeightOffset = 1.2f, GroundHeight = _terrain.GroundHeight };
        _camera.Distance = 9f;
        _camController = new FollowCameraController(_camera);
        sc.CameraOverride = _camera;

        // Load the committed CC0 prop kit through the asset pipeline (decompressed glTF -> normalized to its
        // manifest heightMeters with the origin dropped to the base), one uploaded mesh per id. Each prop's
        // collision footprint is derived from its actual mesh (PropFootprint): a tree's trunk slice, a rock's full
        // footprint - so colliders are correctly sized with no hand-authored radii (an explicit manifest collider
        // would still win).
        string manifestPath = Path.Combine(AppContext.BaseDirectory, "assets", "props", "props.manifest.json");
        AssetManifest manifest = AssetManifest.Load(manifestPath);
        var colliderShapes = new Dictionary<string, ColliderShape>();
        foreach (AssetEntry entry in manifest.Props)
        {
            GltfMesh mesh = PropLoader.LoadProp(entry);
            _propMeshes[entry.Id] = sc.LoadMesh(mesh);
            colliderShapes[entry.Id] = entry.Collider ?? PropFootprint.Derive(mesh);
        }

        // Static-world collision: make the nearby scattered props solid, plus a hand-placed "inn" box sitting in
        // the clearing so the box collider path is demonstrable. Built from the SAME deterministic scatter the
        // streamer renders (so colliders line up with the visible trees), over a fixed ring around spawn
        // (streaming colliders is a later piece).
        var colliderArea = new RectArea(-120f, -120f, 120f, 120f);
        IReadOnlyList<PropPlacement> colliderPlacements = PropScatter.Generate(_field, ScatterConfig.ForestRing(), colliderArea);
        var inn = WorldCollider.Box(new Vector2(0f, 12f), new Vector2(3f, 2.5f), yaw: 0f);
        _colliders = PropColliders.FromScatter(
            colliderPlacements,
            id => colliderShapes.TryGetValue(id, out ColliderShape s) ? s : (ColliderShape?)null,
            obstacles: new[] { inn });
        Console.WriteLine($"Static collision: {_colliders.Count} solid colliders (mesh-derived prop footprints + 1 building). Walk into a tree or the inn (12 m north, +Z) - you can't pass through.");

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

        // --- rendering consult: live A/B of the outline knobs ---
        var post = Scene.Post;
        if (Input.WasPressed(Key.M))
        {
            post.RenderScale = post.RenderScale == RenderScale.MatchViewport
                ? RenderScale.FixedInternal : RenderScale.MatchViewport;
            Console.WriteLine($"[post] RenderScale = {post.RenderScale}");
        }
        if (Input.WasPressed(Key.O))
        {
            post.Outline = !post.Outline;
            Console.WriteLine($"[post] Outline = {post.Outline}");
        }
        if (Input.WasPressed(Key.L)) { post.OutlineDepthThreshold = MathF.Min(2f, post.OutlineDepthThreshold + 0.05f); Console.WriteLine($"[post] OutlineDepthThreshold = {post.OutlineDepthThreshold:0.00}"); }
        if (Input.WasPressed(Key.K)) { post.OutlineDepthThreshold = MathF.Max(0f, post.OutlineDepthThreshold - 0.05f); Console.WriteLine($"[post] OutlineDepthThreshold = {post.OutlineDepthThreshold:0.00}"); }
        if (Input.WasPressed(Key.H)) { post.OutlineNormalThreshold = MathF.Min(2f, post.OutlineNormalThreshold + 0.05f); Console.WriteLine($"[post] OutlineNormalThreshold = {post.OutlineNormalThreshold:0.00}"); }
        if (Input.WasPressed(Key.G)) { post.OutlineNormalThreshold = MathF.Max(0f, post.OutlineNormalThreshold - 0.05f); Console.WriteLine($"[post] OutlineNormalThreshold = {post.OutlineNormalThreshold:0.00}"); }

        _character.Update(Input, dt, _camera.Yaw, _terrain.GroundHeight, _terrain.GroundNormal, _colliders);   // slope gate always on (can't climb cliffs/rim); props + inn are solid

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
