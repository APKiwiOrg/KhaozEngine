using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Dungeon;
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
    /// <summary>The walkable dungeon-generator demo: a procedurally generated <see cref="DungeonLayout"/>
    /// stamped through <see cref="DungeonStamp"/> with the greybox kit (<see cref="DungeonKitMap.Greybox"/>)
    /// and rendered as instanced props, matching Room3D's pattern for a hand-placed (non-streamed) prop set.
    /// Renders through the showcase's shared Scene3D (injected via Init). Esc returns to the menu.
    ///
    /// Collision: <see cref="DungeonStampResult.Statics"/> (merged wall runs, floor-slab runs, and the pitched
    /// stair ramps - already sized and placed off the same <c>DungeonConfig.CellSizeMeters</c>/
    /// <c>FloorHeightMeters</c> the layout was generated with) is registered with a <see cref="BepuPhysicsWorld"/>,
    /// exactly like Room3D's prop statics. <c>CharacterController3D</c> still needs a single-valued
    /// <c>Func&lt;float, float, float&gt; groundHeight</c>, matching <c>TerrainCollision</c>'s continuous
    /// heightfield model - exactly one ground Y per (x, z). A generated dungeon has no such single-valued
    /// analogue: floors stack vertically and a floor-1 room can sit directly above a floor-0 corridor at the
    /// same (x, z). So <see cref="GroundHeight"/> stays a flat fallback at the entrance floor's world Y (never
    /// varies with x/z). The real per-position support (standing on a floor slab, climbing a stair ramp) comes
    /// from the physics statics via <c>CharacterMovement</c>'s downward prop sweep, which takes the HIGHER of
    /// the analytic ground and the physics floor every tick - so the flat fallback only matters where no static
    /// is underfoot (there is none in a fully-stamped layout, but it keeps the character from ever falling
    /// through into the void on a rules edge case).</summary>
    public sealed class RoomDungeon : GameScene, IGameScene3D
    {
        const float CapsuleRadius = 0.3f;
        const float CapsuleHalfHeight = 0.9f;     // 1.8 m total (height 1.2 + 2*radius 0.6)

        // Max yaw turn rate (rad/s) when facing toward horizontal motion, matching Room3D's animated-character
        // facing turn bound (a one-frame collision jitter cannot snap the model).
        const float MaxTurnRate = 12f;

        // The dungeon's props are hand-placed once at generation time and drawn every frame (not streamed),
        // same as Room3D's town buildings. The draw radius just needs to comfortably cover the generated
        // plot regardless of where the player wanders; the 128x128-tile plot at 3 m cells spans at most
        // ~384 m per side, so 1000 m never culls anything in this demo.
        const float PropDrawRadius = 1000f;

        // Sized up for a grand, cavernous feel: same 12-room count as before, but each room spans roughly
        // 24-60 m (RoomMinTiles/RoomMaxTiles at CellSizeMeters), floors are 6 m tall, and the plot is large
        // enough that 12 big rooms place cleanly without saturating. Verified via `ke-dungeon generate`
        // (tools/KeDungeon) at seed 2026: roomsPlaced: 12, saturated: false (also checked seeds 1, 7, 42).
        readonly DungeonConfig _config = new()
        {
            RoomCountTarget = 12,
            RoomMinTiles = 8,
            RoomMaxTiles = 20,
            MaxFloors = 2,
            PlotWidthTiles = 128,
            PlotDepthTiles = 128,
            CellSizeMeters = 3f,
            FloorHeightMeters = 6f,
            LockCount = 1,
        };
        const ulong Seed = 2026UL;

        Scene3D _scene = null!;
        Texture2D _white = null!;
        DpiFont _hud = null!;

        // Guards OnExit against running before OnEnter has built the per-enter state (and OnEnter against
        // leftover state from a previous visit), matching Room3D's re-entry guard.
        bool _built;

        FollowCamera3D _camera = null!;
        FollowCameraController _camController = null!;
        CharacterController3D _character = null!;
        MeshHandle _capsule;

        // One uploaded mesh per kit id (dungeon_floor/wall/doorframe/stair/landing), and the flat prop-placement
        // list stamped from the generated layout. Not streamed: the whole dungeon is drawn every frame.
        readonly Dictionary<string, MeshHandle> _kitMeshes = new();
        List<PropPlacement> _placements = null!;

        // Fixed ground height for this walk demo (see the type doc's collision-decision note): the entrance
        // floor's world Y, constant regardless of where the player walks.
        float _groundY;

        // Physics world holding the dungeon's stamped collision statics (walls, floor slabs, stair ramps),
        // stepped each frame and consulted by CharacterController3D, matching Room3D's pattern.
        BepuPhysicsWorld _physics = null!;

        // Animated character (replaces the static capsule when the asset loads). Falls back to the greybox
        // capsule if the asset is missing/unreadable, matching Room3D's TryLoadAnimatedCharacter.
        SkinnedMeshHandle _characterMesh;
        AnimatedCharacter _animChar = null!;
        bool _animated;
        float _charScale = 1f;
        float _facingYaw;
        Vector3 _prevCharPos;

        public RoomDungeon Init(Scene3D scene, Texture2D white, DpiFont hud)
        {
            _scene = scene; _white = white; _hud = hud;
            return this;
        }

        public override void OnEnter()
        {
            DungeonLayout layout = DungeonGenerator.Generate(_config, Seed);
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f); // identity transform: plot origin at the world origin
            DungeonStampResult stamp = DungeonStamp.Build(layout, kit, plot);

            // Load the committed greybox dungeon kit through the same asset pipeline Room3D uses for its prop
            // kit, one uploaded mesh per id.
            string dungeonDir = Path.Combine(AppContext.BaseDirectory, "assets", "dungeon");
            AssetManifest manifest = AssetManifest.Load(Path.Combine(dungeonDir, "dungeon.manifest.json"));
            foreach (AssetEntry entry in manifest.Props)
                _kitMeshes[entry.Id] = _scene.LoadMesh(PropLoader.LoadProp(entry));

            // DungeonPropInstance -> PropPlacement so the stamped props can go through the same instanced
            // DrawProps path Room3D uses for its town buildings (variant is unused by DungeonStamp; always 0).
            _placements = new List<PropPlacement>(stamp.Props.Count);
            foreach (DungeonPropInstance p in stamp.Props)
                _placements.Add(new PropPlacement(p.KitId, p.X, p.Y, p.Z, p.Scale, p.Yaw, variant: 0));

            _capsule = _scene.LoadMesh(MeshPrimitives.Capsule(radius: CapsuleRadius, height: 1.2f, segments: 16, rings: 6));

            // Physics world for the dungeon's collision statics: every wall run, floor-slab run, and pitched
            // stair ramp DungeonStamp built, fed straight in (already sized/placed off the layout's own
            // CellSizeMeters/FloorHeightMeters, so this matches the visual stamp exactly), matching Room3D's
            // AddStatic pattern for its town-building/prop collision.
            _physics = new BepuPhysicsWorld();
            foreach ((PhysicsShape shape, Pose pose) in stamp.Statics)
                _physics.AddStatic(shape, pose);

            // Spawn at the entrance marker tile (the generator always places exactly one Entrance marker, at
            // the entrance room's center tile on floor 0; see MarkerPlanner.PlanMarkers).
            DungeonMarker entranceMarker = layout.Markers.First(m => m.Type == DungeonMarkerType.Entrance);
            (float ex, float ey, float ez) = plot.TileCenter(entranceMarker.Tile, layout.CellSizeMeters, layout.FloorHeightMeters);
            _groundY = ey;

            _character = new CharacterController3D { CapsuleHalfHeight = CapsuleHalfHeight, CapsuleRadius = CapsuleRadius };
            _character.SetXZ(ex, ez);
            _character.Update(InputState.Empty, 0f, 0f, GroundHeight, GroundNormal, _physics);
            _prevCharPos = _character.Position;

            // Animated character: skinned-ingest the committed Quaternius Universal CC0 rig. The capsule stays
            // as a fallback if the asset is missing/unreadable.
            TryLoadAnimatedCharacter(_scene);

            _camera = new FollowCamera3D { Target = _character.Position, HeightOffset = 1.2f };
            _camera.Distance = 9f;
            _camController = new FollowCameraController(_camera);
            _scene.CameraOverride = _camera;

            // Step the physics world once so Bepu's broad phase is current before the first rendered frame,
            // matching Room3D's post-load priming step.
            _physics.Step(1f / 30f);

            _built = true;
        }

        // Constant ground height (see the type doc's collision-decision note): no per-(x, z) sampling, the
        // whole demo's analytic fallback sits at the entrance floor's Y, and the physics statics carry the real
        // per-position support.
        float GroundHeight(float x, float z) => _groundY;

        // Constant up normal paired with the flat GroundHeight above: there is no per-(x, z) slope analogue for
        // a stacked-floor dungeon, so this always reads flat (never gates a move) - passed for shape parity with
        // Room3D's terrain groundNormal delegate, not because the dungeon has an analytic slope to report.
        static Vector3 GroundNormal(float x, float z) => Vector3.UnitY;

        public override void OnUpdate(float dt)
        {
            if (Manager!.Input.WasPressed(Key.Escape)) { Manager!.Pop(); return; }

            // Physics world ticks once per frame before movement, matching Room3D's ordering.
            _physics.Step(dt);

            _character.Update(Manager!.Input, dt, _camera.Yaw, GroundHeight, GroundNormal, _physics);

            // Animate the character off the same movement state as Room3D: horizontal speed from the XZ
            // position delta over dt (reflects collision-clamped motion), facing turned toward the move
            // direction at a bounded rate, vertical state straight from the controller. Client-cosmetic.
            if (_animated && dt > 1e-5f)
            {
                Vector3 cur = _character.Position;
                Vector3 d = cur - _prevCharPos; d.Y = 0f;
                float horizSpeed = d.Length() / dt;
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

            _camera.Target = _character.Position;
            _camera.AspectRatio = Manager!.FrameHeight > 0 ? (float)Manager!.FrameWidth / Manager!.FrameHeight : _camera.AspectRatio;
            _camController.Update(Manager!.Input, dt);
        }

        public void OnDraw3D(Scene3D scene)
        {
            scene.DrawProps(_placements, _kitMeshes, _character.Position, PropDrawRadius);

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
        }

        // Tears down everything OnEnter built into the shared Scene3D, so the menu/other rooms render cleanly
        // and a re-entry rebuilds from scratch, matching Room3D's teardown.
        public override void OnExit()
        {
            if (!_built) return;
            _built = false;

            _physics.Dispose();

            _scene.UnloadMesh(_capsule);
            foreach (MeshHandle h in _kitMeshes.Values) _scene.UnloadMesh(h);
            _kitMeshes.Clear();
            _placements = null!;
            if (_animated) _scene.UnloadSkinnedMesh(_characterMesh);

            _scene.CameraOverride = null;

            _physics = null!;
            _characterMesh = default;
            _animChar = null!;
            _animated = false;
        }

        // Normalize an angle to (-pi, pi] so the facing turn always takes the shortest path. Matches Room3D's helper.
        static float Wrap(float a)
        {
            a %= MathF.Tau;
            if (a > MathF.PI) a -= MathF.Tau;
            else if (a <= -MathF.PI) a += MathF.Tau;
            return a;
        }

        // Skinned-ingest the committed Quaternius Universal CC0 character + its animation clips, map the clip
        // names to the locomotion states, and build the AnimatedCharacter. On any failure the room keeps the
        // capsule. Matches Room3D's TryLoadAnimatedCharacter exactly (same asset, same clip mapping).
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
                Map(LocomotionState.SwimIdle, "SwimIdle"); // tread water (absent in this rig -> degrades to Idle)
                Map(LocomotionState.Swim, "Swim");         // forward stroke (absent -> degrades to Idle)
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
