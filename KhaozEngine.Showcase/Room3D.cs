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

        Scene3D _scene = null!;
        Texture2D _white = null!;
        SpriteFont _hud = null!;

        TerrainField _field = null!;
        TerrainCollision _terrain = null!;
        FollowCamera3D _camera = null!;
        FollowCameraController _camController = null!;

        // One uploaded mesh per prop-kit id. The streamer scatters per-chunk props from these on demand.
        readonly Dictionary<string, MeshHandle> _propMeshes = new();

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
            _field = new TerrainField(TerrainPresets.BoundedClearing());
            _terrain = new TerrainCollision(_field);

            // 1.8 m greybox capsule (height 1.2 + 2*radius 0.6); mesh bottom sits at y=0 in local space.
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

            _sink = new Scene3DChunkSink(_scene, _field, ScatterConfig.ForestRing(), _propMeshes,
                chunkSize: TerrainChunkRegion.DefaultSize, propDrawRadius: PropDrawRadius, material: terrainMaterial,
                physics: _physics, collisionShapes: collisionShapes);
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
            // and the vertical state straight from the controller. Client-cosmetic; no effect on movement/collision.
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

        public override void OnExit()
        {
            // Task 6: dispose streamer + physics, clear _scene.CameraOverride, reset _scene.Post.
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
