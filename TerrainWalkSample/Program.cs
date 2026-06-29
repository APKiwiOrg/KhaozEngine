using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Windowing;

// Walkable overworld slice: drive an animated Quaternius Universal CC0 character over the shipped analytic terrain
// (TerrainPresets.Clearing) with a third-person follow camera. WASD move, mouse-drag orbit,
// scroll zoom, shift run, Esc quit. The terrain field is wrapped in TerrainCollision for the
// ground-clamp; the world is STREAMED (TerrainStreamer loads/unloads chunks + their props in a
// ring around the player, so walking any direction streams the world forever) and the character
// idles/walks/runs and jumps/falls via AnimatedCharacter off the controller's movement state (it
// falls back to a greybox capsule if the asset fails to load). Honors KE_MAX_FRAMES so a headless
// smoke run renders N frames then exits 0.
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
    MeshHandle _platformMesh;
    Matrix4x4 _platformXform;

    // Animated character (replaces the static capsule). The Quaternius Universal CC0 rig is skinned-ingested so its animation
    // channels survive; AnimatedCharacter drives idle/walk/run/jump/fall off the same movement state the controller
    // computes. Falls back to the greybox capsule if the asset is missing/unreadable.
    SkinnedMeshHandle _characterMesh;
    AnimatedCharacter _animChar = null!;
    bool _animated;
    float _charScale = 1f;
    float _facingYaw;
    Vector3 _prevCharPos;

    // One uploaded mesh per kit id; the streamer scatters per-chunk props from these on demand.
    readonly Dictionary<string, MeshHandle> _propMeshes = new();

    // Streams terrain chunks + props in a ring around the player so the world is effectively endless.
    TerrainStreamer _streamer = null!;
    Scene3DChunkSink _chunkSink = null!;

    CharacterController3D _character = null!;
    FollowCamera3D _camera = null!;
    FollowCameraController _camController = null!;

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
        _prevCharPos = _character.Position;

        // Animated character: skinned-ingest the committed Quaternius Universal CC0 rig (LoadSkinned + LoadAnimations
        // preserve the rig + animation channels - NOT the flatten-prop path), map its clip names to the locomotion states, and
        // build an AnimatedCharacter. The capsule stays as a fallback if the asset is missing/unreadable.
        TryLoadAnimatedCharacter(sc);

        // Follow camera drives rendering via the scene override; the ground delegate keeps the eye above the
        // terrain so it does not sink through the floor when the character is in a dip.
        _camera = new FollowCamera3D { Target = _character.Position, HeightOffset = 1.2f, GroundHeight = _terrain.GroundHeight };
        _camera.Distance = 9f;
        _camController = new FollowCameraController(_camera);
        sc.CameraOverride = _camera;

        // Load the committed CC0 prop kit through the asset pipeline (decompressed glTF -> normalized to its
        // manifest heightMeters with the origin dropped to the base), one uploaded mesh per id. Prop collision
        // footprints are derived from the actual mesh geometry (PropFootprint) - explicit manifest entries still
        // win, but for most props this gives correct sizing automatically.
        string manifestPath = Path.Combine(AppContext.BaseDirectory, "assets", "props", "props.manifest.json");
        AssetManifest manifest = AssetManifest.Load(manifestPath);
        foreach (AssetEntry entry in manifest.Props)
        {
            GltfMesh mesh = PropLoader.LoadProp(entry);
            _propMeshes[entry.Id] = sc.LoadMesh(mesh);
        }

        // A hand-placed visible platform box in the clearing so there is something to look at.
        const float platformHeight = 1.0f;
        var platformCenter = new Vector2(0f, 12f);
        var platformHalf = new Vector2(3f, 2.5f);
        float platformBaseY = _terrain.GroundHeight(platformCenter.X, platformCenter.Y);
        _platformMesh = sc.LoadMesh(MeshPrimitives.Box(1f));
        _platformXform = Matrix4x4.CreateScale(2f * platformHalf.X, platformHeight, 2f * platformHalf.Y)
                         * Matrix4x4.CreateTranslation(platformCenter.X, platformBaseY + platformHeight * 0.5f, platformCenter.Y);

        Console.WriteLine("Static-world physics not yet wired (IPhysicsWorld adoption in progress). Terrain-only collision active.");

        // Endless streamed world: the sink builds chunk meshes + deterministic per-chunk props (same coordinate-hash
        // scatter as before, now over every chunk's area), the streamer keeps a ring of them loaded around the player
        // within a per-frame budget. Prime the first ring before the first frame so the player spawns on solid ground.
        // Textured terrain (PBR splat). A procedural placeholder material so the sample needs no binary assets;
        // real games wire CC0 tileable albedo/normal per layer.
        var terrainMaterial = sc.LoadTerrainMaterial(TerrainMaterialPresets.Procedural());
        _chunkSink = new Scene3DChunkSink(sc, _field, ScatterConfig.ForestRing(), _propMeshes,
            chunkSize: TerrainChunkRegion.DefaultSize, propDrawRadius: PropDrawRadius, material: terrainMaterial);
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

        _character.Update(Input, dt, _camera.Yaw, _terrain.GroundHeight, _terrain.GroundNormal);   // slope gate on; prop physics not yet wired

        // Animate the character off the same movement state: horizontal speed from the XZ position delta over dt
        // (so it reflects collision-clamped motion, not just input), facing turned toward the move direction, and
        // the vertical state straight from the controller. Client-cosmetic; no effect on movement/collision.
        if (_animated && dt > 1e-5f)
        {
            Vector3 cur = _character.Position;
            Vector3 d = cur - _prevCharPos; d.Y = 0f;
            float horizSpeed = d.Length() / dt;
            if (d.LengthSquared() > 1e-6f) _facingYaw = MathF.Atan2(d.X, d.Z);
            _animChar.Update(horizSpeed, _character.Grounded, _character.VerticalVelocity, dt);
            _prevCharPos = cur;
        }

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

        // The hand-placed visible platform.
        scene.Draw(_platformMesh, _platformXform, new Color(0.62f, 0.6f, 0.66f, 1f));

        // Draw the character so its feet sit on the ground (Position is the capsule centre; feet = centre - half).
        Vector3 p = _character.Position;
        float footY = p.Y - CapsuleHalfHeight;
        if (_animated)
        {
            Matrix4x4 model = Matrix4x4.CreateScale(_charScale)
                              * Matrix4x4.CreateRotationY(_facingYaw)
                              * Matrix4x4.CreateTranslation(p.X, footY, p.Z);
            scene.DrawSkinned(_characterMesh, _animChar.Pose, model, Color.White);
        }
        else
        {
            scene.Draw(_capsule, Matrix4x4.CreateTranslation(p.X, footY, p.Z), new Color(0.85f, 0.55f, 0.25f, 1f));
        }
    }

    // Skinned-ingest the committed Quaternius Universal CC0 character + its animation clips, map the clip names to the
    // locomotion states, and build the AnimatedCharacter. On any failure the sample keeps the capsule.
    void TryLoadAnimatedCharacter(Scene3D sc)
    {
        try
        {
            string charPath = Path.Combine(AppContext.BaseDirectory, "assets", "character", "Player.glb");
            (SkinnedGltfMesh charMesh, GltfMaterialMaps charMaps) = GltfLoader.LoadSkinnedWithMaterial(charPath);
            if (charMesh.Skeleton is null) { Console.WriteLine("Character has no skeleton; using the capsule."); return; }
            _characterMesh = sc.LoadSkinnedMesh(charMesh, charMaps);

            var byName = new Dictionary<string, AnimationClip>();
            foreach (AnimationClip c in GltfLoader.LoadAnimations(charPath)) byName[c.Name] = c;
            var clips = new Dictionary<LocomotionState, AnimationClip>();
            void Map(LocomotionState st, string name) { if (byName.TryGetValue(name, out AnimationClip? c)) clips[st] = c; }
            Map(LocomotionState.Idle, "Idle");
            Map(LocomotionState.Walk, "Walk");
            Map(LocomotionState.Run, "Run");           // a forward jog
            Map(LocomotionState.Jump, "Jump");         // airborne loop (rising)
            Map(LocomotionState.Fall, "Fall");         // airborne loop (descending)
            if (clips.Count == 0) { Console.WriteLine("Character has no expected clips; using the capsule."); return; }

            // Scale the model to ~the 1.8 m capsule height so it lines up with the camera + collision footprint.
            float modelHeight = ModelHeight(charMesh);
            _charScale = modelHeight > 0.01f ? (CapsuleHalfHeight * 2f) / modelHeight : 1f;

            // Thresholds split walk/run between the controller's 3 m/s walk and 6 m/s run.
            _animChar = new AnimatedCharacter(charMesh.Skeleton, clips, new LocomotionThresholds(0.1f, 4.5f), crossfade: 0.15f);
            _animated = true;
            Console.WriteLine($"Animated character: {charMesh.BoneCount} bones, states [{string.Join(", ", clips.Keys)}], scale {_charScale:0.00}.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Character load failed ({e.Message}); falling back to the capsule.");
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
}
