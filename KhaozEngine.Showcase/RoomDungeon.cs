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

        // Fixed ground height for this walk demo (see the type doc's collision-decision note): the entrance
        // floor's world Y, constant regardless of where the player walks.
        float _groundY;

        // Physics world holding the dungeon's stamped collision statics (walls, floor slabs, stair ramps),
        // stepped each frame and consulted by CharacterController3D, matching Room3D's pattern.
        BepuPhysicsWorld _physics = null!;

        // Turnkey animated character: CharacterAvatar (KhaozEngine.Game.Render3D) composes the controller + animation
        // + collision-robust facing + draw. Null while the rig asset is missing/unreadable, in which case the room
        // falls back to drawing the greybox capsule.
        CharacterAvatar? _avatar;

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
            // existed only because the previous two-cell run sat exactly on the 45-degree limit.)
            _character = new CharacterController3D
            {
                CapsuleHalfHeight = CapsuleHalfHeight,
                CapsuleRadius = CapsuleRadius,
            };
            _character.SetXZ(ex, ez);
            _character.Update(InputState.Empty, 0f, 0f, GroundHeight, GroundNormal, _physics);

            // Wrap the controller in a CharacterAvatar: it skinned-ingests the committed Quaternius Universal CC0 rig,
            // maps its clips, scales it to the capsule, and owns the facing + animation + draw. Null on any load
            // failure, so the room keeps the greybox capsule.
            _avatar = CharacterAvatar.TryLoadGltf(_scene,
                Path.Combine(AppContext.BaseDirectory, "assets", "character", "Player.glb"), _character,
                onFailure: reason => Console.WriteLine($"Character load failed ({reason}); falling back to the capsule."));
            if (_avatar is not null)
                Console.WriteLine($"Animated character loaded (scale {_avatar.ModelScale:0.00}).");

            _camera = new FollowCamera3D { Target = _character.Position, HeightOffset = 1.2f };
            _camera.Distance = 9f;
            _camera.Occlusion = _physics;   // spring-arm: pull the eye in through a wall/ceiling rather than clip it
            // Demo-only: smooth the camera's follow target. The avatar's RenderPosition already eases the discrete
            // stair-step HEIGHT snaps, but its physics XZ still carries a small, un-smoothed per-riser fore-aft
            // stutter (the paced step-climb advances forward in a lumpy [0, walk-step] cadence, which the character
            // fix keeps monotone-forward but cannot flatten without lowering the mount cap and re-stalling). A light
            // target damping on the CAMERA glides over that residual so the dungeon-stair view reads smooth. Off by
            // engine default; enabled here (and in Room3D) only, leaving every other consumer's camera untouched.
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

            // Drive the character. The avatar (when the rig loaded) owns movement + collision-robust facing (stable
            // against the tight stairwell walls, so the model never spins) + animation in a single call; without it,
            // drive the controller alone for the greybox capsule.
            if (_avatar is not null)
                _avatar.Update(Manager!.Input, dt, _camera.Yaw, GroundHeight, GroundNormal, _physics);
            else
                _character.Update(Manager!.Input, dt, _camera.Yaw, GroundHeight, GroundNormal, _physics);

            // Target the avatar's presentation position (physics XZ + smoothed height) so the camera glides on stairs.
            _camera.Target = _avatar?.RenderPosition ?? _character.Position;
            _camera.AspectRatio = Manager!.FrameHeight > 0 ? (float)Manager!.FrameWidth / Manager!.FrameHeight : _camera.AspectRatio;
            _camController.Update(Manager!.Input, dt);
        }

        public void OnDraw3D(Scene3D scene)
        {
            scene.DrawProps(_placements, _kitMeshes, _character.Position, PropDrawRadius);

            // Draw the character. The avatar draws its skinned mesh at the feet with the right facing/scale; without
            // it, draw the greybox capsule (Position is the capsule centre, feet = centre - half).
            if (_avatar is not null)
            {
                _avatar.Draw(scene);
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
            if (_avatar is not null) _scene.UnloadSkinnedMesh(_avatar.Mesh);

            _scene.CameraOverride = null;

            // Reset the Post field this room's OnUpdate can mutate, back to PixelPostProcessSettings's own
            // default (Post has no setter, it is a shared instance owned by Scene3D, so the field is reset
            // individually rather than reassigning the property), matching Room3D's OnExit.
            _scene.Post.Outline = false;

            _physics = null!;
            _avatar = null;
        }
    }
}
