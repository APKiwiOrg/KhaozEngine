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
            CeilingMode = DungeonCeilingMode.Roofed,   // roofed interiors: the walk reads as an enclosed cave
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

        // One uploaded mesh per kit id (dungeon_floor/wall/doorframe/stair/landing/ceiling), and the flat
        // prop-placement list stamped from the generated layout. Not streamed: the whole dungeon is drawn every frame.
        readonly Dictionary<string, MeshHandle> _kitMeshes = new();
        List<PropPlacement> _placements = null!;

        // The intended-facing target the player last commanded (radians), held while stationary - mirrors
        // CharacterFacing.TurnTowards' zero-direction hold so the character never spins to face yaw 0 when the move
        // keys are released. Fed to the character bridge as an explicit CharacterSample.FacingYaw each frame so the
        // model faces where the player is PUSHING (collision-robust: stable against the tight stairwell walls, never
        // the collision-slid velocity), while the bridge's own CharacterAnimatorTuning.YawSmoothing eases the turn.
        float _facingYaw;

        // Local running sum of the sim's per-tick discrete-step impulse (CharacterController3D.StepDeltaY), fed to the
        // bridge as CharacterSample.StepCumulativeY. No netcode reconciliation runs in this room (one direct
        // CharacterController3D.Update per frame, no predict/replay), so a plain accumulator is exactly-once by
        // construction - the same guarantee ClientPrediction.StepCumulativeY provides over the network.
        float _stepCumulativeY;

        // Fixed ground height for this walk demo (see the type doc's collision-decision note): the entrance
        // floor's world Y, constant regardless of where the player walks.
        float _groundY;

        // Physics world holding the dungeon's stamped collision statics (walls, floor slabs, stair ramps),
        // stepped each frame and consulted by CharacterController3D, matching Room3D's pattern.
        BepuPhysicsWorld _physics = null!;

        // The canonical signal-driven stair-glide presentation: ReplicatedCharacterAnimators (KhaozEngine.Game.Render3D,
        // "the character bridge") owns one AnimatedCharacter brain for this room's single local entity (id 0), driven
        // each frame from an exact-movement CharacterSample built off _character's state (see OnUpdate). It glides the
        // drawn feet up/down stairs from the sim's own ClimbRate signal (never a position-delta estimate) and eases
        // isolated steps via the UE-style step-event mesh offset (StepCumulativeY) - the same bridge RoomNet drives for
        // networked players, adopted here for a local one. _animated is false (and _animators null) while the rig
        // asset is missing/unreadable, in which case the room falls back to drawing the greybox capsule at the raw
        // physics position.
        SkinnedMeshHandle _characterMesh;
        ReplicatedCharacterAnimators? _animators;
        bool _animated;
        readonly List<CharacterSample> _samples = new(1);

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
            // The feet-on-floor alignment (dropping the floor/landing pieces so their visible top lands on the
            // physics floor slab's top) is now done in the engine via PieceMapper.FloorPieceYOffset, so it is
            // correct here AND in baked MapDocs - no demo-side shift.
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

            // Default MaxSlopeRadians (45 deg) is left as-is: DungeonStamp now pitches every stair ramp over a
            // three-cell run at ~34 degrees (atan(FloorHeightMeters / (3*CellSizeMeters)) = atan(6/9)), comfortably
            // walkable at the engine default, so no per-game slope override is needed. (The old 52-degree override
            // existed only because the previous two-cell run sat exactly on the 45-degree limit.) Default StepHeight
            // (0.4 m) and MaxStepClimbSpeed (3.5 m/s) are also left as-is: DungeonStamp.BuildStairSteps caps every
            // riser at 0.34 m (FloorHeightMeters 6 / 18 steps = 0.333 m), comfortably under the 0.4 m step-mount limit,
            // so every riser this config generates auto-mounts without a per-room override.
            _character = new CharacterController3D
            {
                CapsuleHalfHeight = CapsuleHalfHeight,
                CapsuleRadius = CapsuleRadius,
            };
            _character.SetXZ(ex, ez);
            _character.Update(InputState.Empty, 0f, 0f, GroundHeight, GroundNormal, _physics);

            _facingYaw = 0f;
            _stepCumulativeY = 0f;
            TryLoadAnimators();

            _camera = new FollowCamera3D { Target = _character.Position, HeightOffset = 1.2f };
            _camera.Distance = 9f;
            _camera.Occlusion = _physics;   // spring-arm: pull the eye in through a wall/ceiling rather than clip it
            // Demo-only: smooth the camera's follow target. The character bridge's signal-driven glide (see OnUpdate)
            // already eases the discrete stair-step HEIGHT snaps from the sim's own ClimbRate fact - no estimation, no
            // duplicate smoothing needed here. But the bridge never smooths X/Z (RenderPosition's horizontal is always
            // the raw sample position, by design - see CharacterPose.World), and the physics XZ still carries a small,
            // un-smoothed per-riser fore-aft stutter (the paced step-climb advances forward in a lumpy [0, walk-step]
            // cadence: CharacterMovement's monotone-forward fix keeps it non-reversing but cannot flatten it without
            // lowering the mount cap and re-stalling - see KhaozEngine.Tests/Dungeon/StairAscentFeelTests). A light
            // target damping on the CAMERA glides over THAT residual (genuinely orthogonal to the height glide above)
            // so the dungeon-stair view reads smooth. Off by engine default, enabled here (and in Room3D) only,
            // leaving every other consumer's camera untouched.
            _camera.EnableTargetDamping = true;
            _camController = new FollowCameraController(_camera);
            _scene.CameraOverride = _camera;

            // Outline post-process starts OFF here by the engine default, so the dungeon shows the plain lit look
            // rather than the stylized cel/outline one. Press O to toggle it on (see OnUpdate); OnExit resets it
            // back to that default for the menu / other rooms.

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

            // Outline post-process toggle, matching Room3D's O key exactly (see OnEnter for why it starts off).
            if (Manager!.Input.WasPressed(Key.O))
            {
                _scene.Post.Outline = !_scene.Post.Outline;
                Console.WriteLine($"[post] Outline = {_scene.Post.Outline}");
            }

            // Physics world ticks once per frame before movement, matching Room3D's ordering.
            _physics.Step(dt);

            // Drive the body directly: CharacterController3D owns movement + climbing + collision, unconditionally
            // (the character bridge below is presentation-only and never touches the sim).
            Vector3 prevPos = _character.Position;
            _character.Update(Manager!.Input, dt, _camera.Yaw, GroundHeight, GroundNormal, _physics);

            Vector3 renderTarget = _character.Position;
            if (_animated && _animators is not null)
            {
                // Feed the character bridge this tick's EXACT sim facts - grounded / vertical velocity / swimming
                // straight from the controller, the climb signal straight from CharacterController3D.ClimbRate (the
                // sim's own fact, never estimated from position deltas), and the discrete-step running sum for the
                // step-event mesh smoothing on isolated risers - so it drives the same signal-driven glide RoomNet
                // drives for networked players, for this single local entity (id 0).
                _stepCumulativeY += _character.StepDeltaY;

                // Hold the last INTENDED facing at rest (see the _facingYaw field doc): collision-robust, so the
                // model never spins against the tight stairwell walls and never snaps to face yaw 0 at a stop.
                Vector3 intended = CharacterFacing.IntendedMoveDirection(Manager!.Input, _camera.Yaw);
                if (intended.LengthSquared() > 1e-6f) _facingYaw = CharacterFacing.YawOf(intended);

                // Exact commanded speed (from the real collision-clamped position delta, not input), so the
                // idle/walk/run state reflects actual motion the same way CharacterAvatar's animation used to.
                Vector3 d = _character.Position - prevPos; d.Y = 0f;
                float horizontalSpeed = dt > 1e-6f ? d.Length() / dt : 0f;

                Vector3 feet = new(_character.Position.X, _character.Position.Y - CapsuleHalfHeight, _character.Position.Z);
                CharacterSample sample = new CharacterSample(0L, feet, isLocal: true, grounded: _character.Grounded,
                    verticalVelocity: _character.VerticalVelocity, planarSpeed: horizontalSpeed, swimming: _character.Swimming,
                    climbRate: _character.ClimbRate, stepCumulativeY: _stepCumulativeY).WithFacingYaw(_facingYaw);

                _samples.Clear();
                _samples.Add(sample);
                _animators.Update(_samples, dt);
                // Point the camera at the bridge's CameraTarget - the signal-driven glide height lifted back to the
                // capsule CENTRE (the sample is feet-anchored, so RenderPosition itself sits a full CapsuleHalfHeight
                // too low: targeting it directly parks the camera at floor level). This is the capsule-centre anchor
                // RoomNet targets via WorldClient.LocalRenderState.Position, but glide-smoothed so the camera rises with
                // the stairs. The discrete-step MESH ease stays on the model only, so a curb never dips the camera.
                if (_animators.Live.Count > 0) renderTarget = _animators.Live[0].CameraTarget(CapsuleHalfHeight);
            }

            // Target the bridge's centre-glide camera anchor (physics XZ + the signal-driven glide height at the capsule
            // centre) so the camera glides on stairs, falling back to the raw physics position when the rig failed to load.
            _camera.Target = renderTarget;
            _camera.AspectRatio = Manager!.FrameHeight > 0 ? (float)Manager!.FrameWidth / Manager!.FrameHeight : _camera.AspectRatio;
            _camController.Update(Manager!.Input, dt);
        }

        public void OnDraw3D(Scene3D scene)
        {
            scene.DrawProps(_placements, _kitMeshes, _character.Position, PropDrawRadius);

            // Draw the character. The bridge's live pose carries its skinned mesh at the right facing/scale/glide
            // height. Without it (rig failed to load), draw the greybox capsule (Position is the capsule centre,
            // feet = centre - half) at the raw physics position.
            if (_animated && _animators is not null && _animators.Live.Count > 0)
            {
                CharacterPose pose = _animators.Live[0];
                scene.DrawSkinned(_characterMesh, pose.Pose, pose.World, new Color(0.85f, 0.55f, 0.25f, 1f));
            }
            else
            {
                Vector3 p = _character.Position;
                float footY = p.Y - CapsuleHalfHeight;
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

            // Reset the Post field this room's OnUpdate can mutate, back to PixelPostProcessSettings's own
            // default (Post has no setter, it is a shared instance owned by Scene3D, so the field is reset
            // individually rather than reassigning the property), matching Room3D's OnExit.
            _scene.Post.Outline = false;

            _physics = null!;
            _characterMesh = default;
            _animators = null;
            _animated = false;
            _samples.Clear();
        }

        // Skinned-ingest the committed Quaternius Universal CC0 character + its clips into the canonical signal-driven
        // stair-glide bridge (ReplicatedCharacterAnimators - the same "character bridge" RoomNet drives for networked
        // players), one local entity (id 0) scaled to this room's capsule. On any load failure the room falls back to
        // the greybox capsule (_animated stays false).
        void TryLoadAnimators()
        {
            try
            {
                string charPath = Path.Combine(AppContext.BaseDirectory, "assets", "character", "Player.glb");
                (SkinnedGltfMesh charMesh, GltfMaterialMaps charMaps) = GltfLoader.LoadSkinnedWithMaterial(charPath);
                if (charMesh.Skeleton is null) { Console.WriteLine("Character has no skeleton, using the capsule."); return; }
                _characterMesh = _scene.LoadSkinnedMesh(charMesh, charMaps);

                var byName = new Dictionary<string, AnimationClip>();
                foreach (AnimationClip c in GltfLoader.LoadAnimations(charPath)) byName[c.Name] = c;
                var clips = new Dictionary<LocomotionState, AnimationClip>();
                void Map(LocomotionState st, string name) { if (byName.TryGetValue(name, out AnimationClip? c)) clips[st] = c; }
                Map(LocomotionState.Idle, "Idle");
                Map(LocomotionState.Walk, "Walk");
                Map(LocomotionState.Run, "Run");
                Map(LocomotionState.Jump, "Jump");
                Map(LocomotionState.Fall, "Fall");
                Map(LocomotionState.SwimIdle, "SwimIdle");   // tread water (absent in this rig -> degrades to Idle)
                Map(LocomotionState.Swim, "Swim");           // forward stroke (absent -> degrades to Idle)
                if (clips.Count == 0)
                {
                    _scene.UnloadSkinnedMesh(_characterMesh);
                    Console.WriteLine("Character has no expected clips, using the capsule.");
                    return;
                }

                // Auto-fit the model to the capsule height (asset-agnostic) and bake that scale into the bridge tuning,
                // starting from CharacterAnimatorTuning.Default so every OTHER tunable (SlopeGlideRate,
                // SlopeGlideSnapDistance, StepSmoothingRate, YawSmoothing, ...) matches the reference adopter exactly.
                float modelHeight = ModelHeight(charMesh);
                float scale = modelHeight > 0.01f ? (CapsuleHalfHeight * 2f) / modelHeight : 1f;
                CharacterAnimatorTuning tuning = CharacterAnimatorTuning.Default;
                tuning.Scale = scale;
                tuning.Locomotion = new LocomotionThresholds(0.1f, 9f);   // matches the controller's 6/12 walk/run feel

                _animators = new ReplicatedCharacterAnimators(charMesh.Skeleton, clips, tuning);
                _animated = true;
                Console.WriteLine($"Animated character loaded ({charMesh.BoneCount} bones, scale {scale:0.00}).");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Character load failed ({e.Message}), falling back to the capsule.");
                _animated = false;
            }
        }

        // Model-space height (max - min Y) of the rest mesh, for the capsule-match scale.
        static float ModelHeight(SkinnedGltfMesh mesh)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (SkinnedVertex v in mesh.Vertices) { min = MathF.Min(min, v.Position.Y); max = MathF.Max(max, v.Position.Y); }
            return max > min ? max - min : 0f;
        }
    }
}
