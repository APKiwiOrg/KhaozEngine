using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
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

        // No props yet (Task 4): an empty prop-mesh set, so the sink scatters nothing. Props arrive in Task 5.
        readonly Dictionary<string, MeshHandle> _propMeshes = new();

        Scene3DChunkSink _sink = null!;
        TerrainStreamer _streamer = null!;

        // Physics world shared by the chunk sink (adds/removes prop statics on stream load/unload, though there are
        // no prop shapes yet - Task 5) and CharacterController3D (resolves the capsule against those statics).
        BepuPhysicsWorld _physics = null!;

        CharacterController3D _character = null!;
        MeshHandle _capsule;

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

            _physics = new BepuPhysicsWorld();

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

            _sink = new Scene3DChunkSink(_scene, _field, ScatterConfig.ForestRing(), _propMeshes,
                chunkSize: TerrainChunkRegion.DefaultSize, propDrawRadius: PropDrawRadius, material: terrainMaterial,
                physics: _physics, collisionShapes: null);
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

        public override void OnDraw2D(SpriteBatch batch)
        {
            // Task 5: HUD here.
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
