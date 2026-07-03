using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Debug;
using KhaozEngine.Terrain;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>One hand-placed town building: which CC0 kit <see cref="Id"/> (must match an entry in
    /// buildings.manifest.json), its world XZ, facing <see cref="Yaw"/> (radians), and uniform <see cref="Scale"/>.
    /// Render-only for now (Task 4 adds collision from the matching baked .coll per id).</summary>
    public readonly record struct TownBuilding(string Id, float X, float Z, float Yaw, float Scale);

    /// <summary>The walkable streamed 3D overworld, ported from TerrainWalkSample into a room. Renders through the
    /// showcase's shared Scene3D (injected via Init, since a GameScene cannot reach the app's 3D surface). Builds
    /// its world in OnEnter and tears it down in OnExit (the Scene3D is shared with the other rooms, so it must
    /// leave no camera override or loaded ring behind). Esc returns to the menu.</summary>
    public sealed class Room3D : GameScene, IGameScene3D
    {
        // distance-cull ring for instanced props around the focus point (matches TerrainWalkSample's PropDrawRadius).
        const float PropDrawRadius = 90f;

        const float CapsuleRadius = 0.3f;
        const float CapsuleHalfHeight = 0.9f;     // 1.8 m total (height 1.2 + 2*radius 0.6)

        // Max yaw turn rate (rad/s) when facing toward horizontal motion (see TerrainWalkSample for the rationale:
        // bounds how fast the model can spin toward a new heading so a one-frame collision jitter cannot snap it).
        const float MaxTurnRate = 12f;

        // Town clearing: a flat plateau near spawn holding the hand-placed buildings. Tuned to sit inside
        // BoundedClearing's rim disc (radius 38) and clear of its lake at (-12,-4). Values are a starting point,
        // adjust by playtest so buildings sit flat and inside the rim.
        static readonly Vector2 TownCenter = new(0f, 14f);
        const float TownRadius = 18f, TownBlend = 0.25f;

        // Draw radius for the 7 hand-placed town buildings: few in count and always meant to be visible from
        // across the clearing, so this is a much wider cull ring than PropDrawRadius (matches Ruinborne's high
        // draw radius for its landmark buildings).
        const float BuildingDrawRadius = 320f;

        // The 7 CC0 Quaternius Medieval Village buildings, hand-placed around TownCenter. Nudged clear of the
        // three fixtures already standing in the clearing: the platform box at (0,12) (half-extents 3x2.5), the
        // procedural textured stone block at (3,3), and the blacksmith collision-proxy fixture at (8,4). All
        // positions sit inside TownRadius * (1 - TownBlend) ~= 13.5 m of TownCenter so they land on the flattened
        // plateau, not its blended rim. Starting layout - exact spacing is tunable by playtest.
        const float BuildingScale = 1.5f;
        static IReadOnlyList<TownBuilding> CreateTownBuildings() => new[]
        {
            // Moved off the platform at (0,12): pushed further north and slightly nudged east.
            new TownBuilding("inn",        TownCenter.X + 1f,  TownCenter.Y + 6f,   0.0f,  BuildingScale),
            new TownBuilding("well",       TownCenter.X + 6f,  TownCenter.Y - 1f,   0.0f,  BuildingScale),
            new TownBuilding("house_1",    TownCenter.X + 9f,  TownCenter.Y + 5f,  -2.2f,  BuildingScale),
            new TownBuilding("house_2",    TownCenter.X - 9f,  TownCenter.Y + 4f,   2.2f,  BuildingScale),
            // Kept clear of the textured stone block (3,3) and the blacksmith proxy fixture (8,4): house_3 sits
            // west of both.
            new TownBuilding("house_3",    TownCenter.X - 5f,  TownCenter.Y - 9f,  -0.7f,  BuildingScale),
            // The blacksmith BUILDING is a separate placement from the blacksmith collision-proxy fixture at
            // (8,4) - pushed further southwest so the two do not visually overlap.
            new TownBuilding("blacksmith", TownCenter.X - 10f, TownCenter.Y - 8f,   0.9f,  BuildingScale),
            new TownBuilding("bell_tower", TownCenter.X,       TownCenter.Y + 11f,  0.0f,  BuildingScale),
        };

        Scene3D _scene = null!;
        Texture2D _white = null!;
        SpriteFont _hud = null!;

        // Guards OnExit against running before OnEnter has built the per-enter state (and OnEnter against
        // leftover state from a previous visit), so the room can be entered and exited repeatedly against the
        // shared Scene3D without doubling meshes/statics or leaking GPU/physics resources.
        bool _built;

        TerrainField _field = null!;
        TerrainCollision _terrain = null!;
        FollowCamera3D _camera = null!;
        FollowCameraController _camController = null!;

        // One uploaded mesh per prop-kit id. The streamer scatters per-chunk props from these on demand.
        readonly Dictionary<string, MeshHandle> _propMeshes = new();

        // Hand-placed town buildings (CC0 Quaternius Medieval Village): one uploaded mesh per building id, plus
        // the fixed placement list built once in OnEnter. Not streamed - drawn directly every frame like the
        // platform/textured-prop fixtures, since there are only 7 and they anchor the clearing.
        readonly Dictionary<string, MeshHandle> _buildingMeshes = new();
        readonly List<PropPlacement> _buildingPlacements = new();

        Scene3DChunkSink _sink = null!;
        TerrainStreamer _streamer = null!;

        // Physics world shared by the chunk sink (adds/removes prop statics on stream load/unload) and
        // CharacterController3D (resolves the capsule against those statics).
        BepuPhysicsWorld _physics = null!;

        CharacterController3D _character = null!;
        MeshHandle _capsule;

        // Hand-placed visible platform box, and its matching static collider.
        MeshHandle _platformMesh;
        Matrix4x4 _platformXform;

        // Textured prop demo: a procedural mossy-stone block (albedo + normal), no binary asset.
        MeshHandle _texturedProp;
        Matrix4x4 _texturedPropXform;

        // Collision-shape debug overlay (F2): a hand-placed building-proxy fixture drawn as translucent proxy
        // meshes over the real collision, plus a legend panel while it is on. Reuses the room's injected
        // _white/_hud rather than owning its own font/texture.
        List<CollisionStatic> _overlayStatics = null!;
        CollisionShapeOverlay _collisionOverlay = null!;
        OverlayLegend _legend = null!;

        // Animated character (replaces the static capsule when the asset loads). Falls back to the greybox capsule
        // if the asset is missing/unreadable, matching TerrainWalkSample's TryLoadAnimatedCharacter.
        SkinnedMeshHandle _characterMesh;
        AnimatedCharacter _animChar = null!;
        bool _animated;
        float _charScale = 1f;
        float _facingYaw;
        Vector3 _prevCharPos;

        public Room3D Init(Scene3D scene, Texture2D white, SpriteFont hud)
        {
            _scene = scene; _white = white; _hud = hud;
            return this;
        }

        public override void OnEnter()
        {
            // Sample the natural ground at the town centre BEFORE flattening (a throwaway field over the
            // un-flattened preset), so the plateau's target height meets the surrounding ground smoothly.
            float townHeight = new TerrainField(TerrainPresets.BoundedClearing()).SampleHeight(TownCenter.X, TownCenter.Y);

            var terrainConfig = TerrainPresets.BoundedClearing();
            var features = new List<ITerrainFeature>(terrainConfig.Features!)
            {
                new FlattenFeature(TownCenter.X, TownCenter.Y, TownRadius, townHeight, TownBlend),
            };
            terrainConfig.Features = features.ToArray();
            _field = new TerrainField(terrainConfig);
            _terrain = new TerrainCollision(_field);

            // 1.8 m greybox capsule (height 1.2 + 2*radius 0.6). Mesh bottom sits at y=0 in local space.
            _capsule = _scene.LoadMesh(MeshPrimitives.Capsule(radius: CapsuleRadius, height: 1.2f, segments: 16, rings: 6));

            // --- Physics world setup ----------------------------------------------------------
            // Build a BepuPhysicsWorld and bake each prop mesh to a PhysicsShape at startup.
            // PropCollisionBake.Bake works from the already-normalized in-memory GltfMesh so no pre-baked
            // .coll files are needed. The shapes dictionary is passed to Scene3DChunkSink. On each chunk
            // load/unload the sink adds/removes the per-placement statics so the character collides against
            // exactly the props in the currently-loaded ring.
            _physics = new BepuPhysicsWorld();
            var collisionShapes = new Dictionary<string, PhysicsShape>();

            // Load the committed CC0 prop kit through the asset pipeline (decompressed glTF -> normalized to its
            // manifest heightMeters with the origin dropped to the base), one uploaded mesh per id. Prop collision
            // footprints are baked from the actual mesh geometry (PropCollisionBake).
            string manifestPath = Path.Combine(AppContext.BaseDirectory, "assets", "props", "props.manifest.json");
            AssetManifest manifest = AssetManifest.Load(manifestPath);
            foreach (AssetEntry entry in manifest.Props)
            {
                GltfMesh mesh = PropLoader.LoadProp(entry);
                _propMeshes[entry.Id] = _scene.LoadMesh(mesh);
                // Bake the collision shape from the same in-memory normalized mesh before it is uploaded.
                collisionShapes[entry.Id] = PropCollisionBake.Bake(mesh);
            }

            // Load the CC0 town buildings (Quaternius Medieval Village) through the same asset pipeline as the
            // forest props, one uploaded mesh per id, and build their fixed world placements. Buildings are not
            // streamed - they are hand-placed once and drawn every frame like the platform/textured-prop fixtures.
            // (Collision is Task 4: these are visual-only for now, the character can walk through them.)
            string bManifestPath = Path.Combine(AppContext.BaseDirectory, "assets", "buildings", "buildings.manifest.json");
            AssetManifest buildings = AssetManifest.Load(bManifestPath);
            foreach (AssetEntry e in buildings.Props)
                _buildingMeshes[e.Id] = _scene.LoadMesh(PropLoader.LoadProp(e));
            foreach (TownBuilding b in CreateTownBuildings())
                _buildingPlacements.Add(new PropPlacement(b.Id, b.X, _terrain.GroundHeight(b.X, b.Z), b.Z, b.Scale, b.Yaw, variant: 0));

            // Character spawns on the ground at the origin. CapsuleRadius matches the visible greybox capsule so
            // the collision footprint lines up with what's drawn.
            _character = new CharacterController3D { CapsuleHalfHeight = CapsuleHalfHeight, CapsuleRadius = CapsuleRadius };
            _character.SetXZ(0f, 0f);
            _character.Update(InputState.Empty, 0f, 0f, _terrain.GroundHeight, _terrain.GroundNormal, _physics);
            _prevCharPos = _character.Position;

            // Animated character: skinned-ingest the committed Quaternius Universal CC0 rig. The capsule stays as a
            // fallback if the asset is missing/unreadable.
            TryLoadAnimatedCharacter(_scene);

            var terrainMaterial = _scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural());

            _camera = new FollowCamera3D { Target = _character.Position, HeightOffset = 1.2f, GroundHeight = _terrain.GroundHeight };
            _camera.Distance = 9f;
            _camController = new FollowCameraController(_camera);
            _scene.CameraOverride = _camera;

            // A hand-placed visible platform box in the clearing so there is something to look at.
            const float platformHeight = 1.0f;
            var platformCenter = new Vector2(0f, 12f);
            var platformHalf = new Vector2(3f, 2.5f);
            float platformBaseY = _terrain.GroundHeight(platformCenter.X, platformCenter.Y);
            _platformMesh = _scene.LoadMesh(MeshPrimitives.Box(1f));
            _platformXform = Matrix4x4.CreateScale(2f * platformHalf.X, platformHeight, 2f * platformHalf.Y)
                             * Matrix4x4.CreateTranslation(platformCenter.X, platformBaseY + platformHeight * 0.5f, platformCenter.Y);

            // Matching static collider so the player can stand on / not walk through the platform.
            // BoxShape takes half-extents. ShapeFactory doubles them to Bepu full extents (3 -> 6).
            // The platform never unloads, so the handle is not retained.
            _physics.AddStatic(
                new BoxShape(new Vector3(platformHalf.X, platformHeight * 0.5f, platformHalf.Y)),
                Pose.At(new Vector3(platformCenter.X, platformBaseY + platformHeight * 0.5f, platformCenter.Y)));

            // Textured prop demo: a procedural mossy-stone block (albedo + normal), no binary asset.
            // Placed near spawn, clear of the platform (0, 12) and the collision-overlay proxy (8, 4).
            // ScaleUv tiles the material 3x across each face so texels stay dense (crisp, not one stretched copy).
            _texturedProp = _scene.LoadMesh(
                MeshOps.WithTangents(MeshOps.ScaleUv(MeshPrimitives.Box(1.5f), 3f)),
                PropMaterialPresets.Procedural());
            float propX = 3f, propZ = 3f;
            _texturedPropXform = Matrix4x4.CreateTranslation(propX, _terrain.GroundHeight(propX, propZ) + 0.75f, propZ);

            // --- Collision-shape debug overlay (F2) ---------------------------------------------
            // Hand-placed building-proxy acceptance fixture: a compound-of-convex .coll baked offline from the
            // Ruinborne blacksmith prop, copied into this sample's assets so no ProjectReference on the test
            // project is needed. Placed near the walk path, clear of the platform (0, 12), with its base on the
            // terrain ground height so it reads as standing on the meadow. Registered as real collision AND kept
            // as a CollisionStatic so the overlay can render translucent proxies over it.
            string proxyFixturePath = Path.Combine(AppContext.BaseDirectory, "assets", "blacksmith_proxy.coll");
            PhysicsShape proxyShape = PropCollisionFormat.Read(proxyFixturePath);
            var proxyPose = new Pose(new Vector3(8f, _terrain.GroundHeight(8f, 4f), 4f), Quaternion.Identity);
            _physics.AddStatic(proxyShape, proxyPose);
            _overlayStatics = new List<CollisionStatic> { new(proxyShape, proxyPose) };

            _collisionOverlay = new CollisionShapeOverlay();
            _collisionOverlay.Build(_scene, _overlayStatics);

            _legend = new OverlayLegend();
            _legend.SetEntries(BuildLegendEntries(_collisionOverlay));

            // Exclude trees from the town: reuse ForestRing's defaults but hole out the flattened plateau so no
            // tree spawns on the levelled ground the buildings sit on.
            var scatterConfig = ScatterConfig.ForestRing();
            scatterConfig.ClearingRadius = TownRadius;
            scatterConfig.ClearingCenter = TownCenter;

            _sink = new Scene3DChunkSink(_scene, _field, scatterConfig, _propMeshes,
                chunkSize: TerrainChunkRegion.DefaultSize, propDrawRadius: PropDrawRadius, material: terrainMaterial,
                ownsMaterial: true, physics: _physics, collisionShapes: collisionShapes);
            _streamer = new TerrainStreamer(StreamerConfig.Default, _sink);

            // Prime the FULL initial ring at load time (this is the loading moment, not a frame, so the per-frame
            // MaxLoadsPerFrame budget is irrelevant here): pump until the loaded set stops growing.
            int loadedBefore = -1;
            while (_streamer.Loaded.Count != loadedBefore)
            {
                loadedBefore = _streamer.Loaded.Count;
                _streamer.Update(_character.Position, 0f);
            }
            // Step the physics world once after the initial ring loads so Bepu's broad phase is current.
            _physics.Step(1f / 30f);

            _built = true;
        }

        public override void OnUpdate(float dt)
        {
            if (Manager!.Input.WasPressed(Key.Escape)) { Manager!.Pop(); return; }

            // --- rendering consult: live A/B of the outline knobs ---
            var post = _scene.Post;
            if (Manager!.Input.WasPressed(Key.M))
            {
                post.RenderScale = post.RenderScale == RenderScale.MatchViewport
                    ? RenderScale.FixedInternal : RenderScale.MatchViewport;
                Console.WriteLine($"[post] RenderScale = {post.RenderScale}");
            }
            if (Manager!.Input.WasPressed(Key.O))
            {
                post.Outline = !post.Outline;
                Console.WriteLine($"[post] Outline = {post.Outline}");
            }
            if (Manager!.Input.WasPressed(Key.L)) { post.OutlineDepthThreshold = MathF.Min(2f, post.OutlineDepthThreshold + 0.05f); Console.WriteLine($"[post] OutlineDepthThreshold = {post.OutlineDepthThreshold:0.00}"); }
            if (Manager!.Input.WasPressed(Key.K)) { post.OutlineDepthThreshold = MathF.Max(0f, post.OutlineDepthThreshold - 0.05f); Console.WriteLine($"[post] OutlineDepthThreshold = {post.OutlineDepthThreshold:0.00}"); }
            if (Manager!.Input.WasPressed(Key.H)) { post.OutlineNormalThreshold = MathF.Min(2f, post.OutlineNormalThreshold + 0.05f); Console.WriteLine($"[post] OutlineNormalThreshold = {post.OutlineNormalThreshold:0.00}"); }
            if (Manager!.Input.WasPressed(Key.G)) { post.OutlineNormalThreshold = MathF.Max(0f, post.OutlineNormalThreshold - 0.05f); Console.WriteLine($"[post] OutlineNormalThreshold = {post.OutlineNormalThreshold:0.00}"); }

            if (Manager!.Input.WasPressed(Key.F2))
            {
                _collisionOverlay.Enabled = !_collisionOverlay.Enabled;
                Console.WriteLine($"[overlay] Collision shape overlay = {_collisionOverlay.Enabled}");
            }

            // Physics world ticks once per frame before movement so newly-streamed props are registered.
            _physics.Step(dt);

            _character.Update(Manager!.Input, dt, _camera.Yaw, _terrain.GroundHeight, _terrain.GroundNormal, _physics);

            // Animate the character off the same movement state: horizontal speed from the XZ position delta over
            // dt (so it reflects collision-clamped motion, not just input), facing turned toward the move direction,
            // and the vertical state straight from the controller. Client-cosmetic, no effect on movement/collision.
            if (_animated && dt > 1e-5f)
            {
                Vector3 cur = _character.Position;
                Vector3 d = cur - _prevCharPos; d.Y = 0f;
                float horizSpeed = d.Length() / dt;
                // Face toward horizontal motion, but turn at a bounded rate so collision jitter cannot spin the model.
                if (d.LengthSquared() > 1e-4f)   // raise the threshold so sub-cm jitter does not steer facing at all
                {
                    float target = MathF.Atan2(d.X, d.Z);
                    float delta = Wrap(target - _facingYaw);          // shortest signed angle, in (-pi, pi]
                    float maxStep = MaxTurnRate * dt;                 // bounded yaw step this frame
                    _facingYaw = Wrap(_facingYaw + Math.Clamp(delta, -maxStep, maxStep));
                }
                _animChar.Update(horizSpeed, _character.Grounded, _character.VerticalVelocity, dt);
                _prevCharPos = cur;
            }

            // Stream the world around the new player position (loads/unloads/re-LODs within MaxLoadsPerFrame).
            _streamer.Update(_character.Position, dt);

            _camera.Target = _character.Position;
            _camera.AspectRatio = Manager!.FrameHeight > 0 ? (float)Manager!.FrameWidth / Manager!.FrameHeight : _camera.AspectRatio;
            _camController.Update(Manager!.Input, dt);
        }

        public void OnDraw3D(Scene3D scene)
        {
            _sink.Draw(_character.Position);

            // The hand-placed visible platform.
            scene.Draw(_platformMesh, _platformXform, new Color(0.62f, 0.6f, 0.66f, 1f));

            // Textured prop demo: procedural mossy-stone block (albedo + normal maps).
            scene.Draw(_texturedProp, _texturedPropXform, Color.White);

            // The hand-placed town buildings (not streamed, always in range at this draw radius).
            scene.DrawProps(_buildingPlacements, _buildingMeshes, _character.Position, BuildingDrawRadius);

            // Draw the character so its feet sit on the ground (Position is the capsule centre, feet = centre - half).
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

            // Translucent collision-shape proxies over the real collision (no-op while disabled).
            _collisionOverlay.Draw(scene);
        }

        public override void OnDraw2D(SpriteBatch batch)
        {
            if (_collisionOverlay.Enabled)
                _legend.Draw(batch, _hud, _white, Manager!.Viewport!.DesignBounds);
        }

        // Maps the overlay's present shape kinds through its palette into legend rows (swatch + name).
        static IReadOnlyList<LegendEntry> BuildLegendEntries(CollisionShapeOverlay overlay)
        {
            var list = new List<LegendEntry>();
            foreach (CollisionShapeKind kind in overlay.PresentKinds)
                list.Add(new LegendEntry(overlay.Palette.For(kind), overlay.Palette.NameFor(kind)));
            return list;
        }

        // Tears down everything OnEnter built into the shared Scene3D, so the menu/2D rooms render cleanly and a
        // re-entry rebuilds from scratch. Guarded so an early exit (before OnEnter finished) is a safe no-op, and
        // idempotent-safe on the disposables themselves (TerrainStreamer.Dispose/CollisionShapeOverlay.Dispose are
        // written to tolerate repeat calls).
        public override void OnExit()
        {
            if (!_built) return;
            _built = false;

            // TerrainStreamer.Dispose flushes the loaded ring through the sink (freeing every chunk mesh) and then
            // disposes the sink itself (it owns Scene3DChunkSink), matching TerrainWalkSample's turn-key teardown.
            // Do not also dispose _sink separately, that would double-dispose it.
            _streamer.Dispose();
            _collisionOverlay.Dispose();
            _physics.Dispose();

            // Free the meshes this room uploaded straight into the shared Scene3D (the streamer freed only the
            // streamed chunk meshes + its owned splat material). Without this each re-entry would leak a fresh copy
            // of the capsule, platform, textured block, character, and prop-kit meshes onto the shared scene.
            _scene.UnloadMesh(_capsule);
            _scene.UnloadMesh(_platformMesh);
            _scene.UnloadMesh(_texturedProp);
            foreach (MeshHandle h in _propMeshes.Values) _scene.UnloadMesh(h);
            foreach (MeshHandle h in _buildingMeshes.Values) _scene.UnloadMesh(h);
            if (_animated) _scene.UnloadSkinnedMesh(_characterMesh);

            // Drop the follow camera so the default camera returns for the menu/2D rooms.
            _scene.CameraOverride = null;

            // Reset every Post field this room's OnUpdate can mutate, back to PixelPostProcessSettings's own
            // defaults (Post has no setter, it is a shared instance owned by Scene3D, so fields are reset
            // individually rather than reassigning the property).
            var post = _scene.Post;
            post.RenderScale = RenderScale.FixedInternal;
            post.Outline = true;
            post.OutlineDepthThreshold = 0.2f;
            post.OutlineNormalThreshold = 0.45f;

            // Null the per-enter fields so a re-entered room rebuilds fresh rather than reusing stale handles.
            _propMeshes.Clear();
            _buildingMeshes.Clear();
            _buildingPlacements.Clear();
            _sink = null!;
            _streamer = null!;
            _physics = null!;
            _collisionOverlay = null!;
            _overlayStatics = null!;
            _legend = null!;
            _characterMesh = default;
            _animChar = null!;
            _animated = false;
        }

        // Normalize an angle to (-pi, pi] so the facing turn always takes the shortest path.
        static float Wrap(float a)
        {
            a %= MathF.Tau;
            if (a > MathF.PI) a -= MathF.Tau;
            else if (a <= -MathF.PI) a += MathF.Tau;
            return a;
        }

        // Skinned-ingest the committed Quaternius Universal CC0 character + its animation clips, map the clip names
        // to the locomotion states, and build the AnimatedCharacter. On any failure the room keeps the capsule.
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
}
