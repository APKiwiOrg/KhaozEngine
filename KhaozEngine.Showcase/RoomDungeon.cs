using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Dungeon;
using KhaozEngine.Game;
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
    /// Collision: this room wires NO physics/statics for the dungeon geometry. <c>CharacterController3D</c>
    /// (see Room3D) requires a single-valued <c>Func&lt;float, float, float&gt; groundHeight</c>, matching
    /// <c>TerrainCollision</c>'s continuous heightfield model - exactly one ground Y per (x, z). A generated
    /// dungeon has no such single-valued analogue: floors stack vertically and a floor-1 room can sit directly
    /// above a floor-0 corridor at the same (x, z), so resolving "the" ground height at a point (or mapping
    /// <see cref="DungeonStampResult.Statics"/> into a walkable collision surface) is a design decision, not a
    /// mechanical reuse of Room3D's plumbing. Skipped per Task 16's brief: the player walks unobstructed
    /// (no walls block movement, no floor collision beyond a fixed height) at the entrance floor's world Y.
    /// Collision fidelity for a playable dungeon is a game-side concern.</summary>
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

            // Spawn at the entrance marker tile (the generator always places exactly one Entrance marker, at
            // the entrance room's center tile on floor 0; see MarkerPlanner.PlanMarkers).
            DungeonMarker entranceMarker = layout.Markers.First(m => m.Type == DungeonMarkerType.Entrance);
            (float ex, float ey, float ez) = plot.TileCenter(entranceMarker.Tile, layout.CellSizeMeters, layout.FloorHeightMeters);
            _groundY = ey;

            _character = new CharacterController3D { CapsuleHalfHeight = CapsuleHalfHeight, CapsuleRadius = CapsuleRadius };
            _character.SetXZ(ex, ez);
            _character.Update(InputState.Empty, 0f, 0f, GroundHeight);

            _camera = new FollowCamera3D { Target = _character.Position, HeightOffset = 1.2f };
            _camera.Distance = 9f;
            _camController = new FollowCameraController(_camera);
            _scene.CameraOverride = _camera;

            _built = true;
        }

        // Constant ground height (see the type doc's collision-decision note): no per-(x, z) sampling, the
        // whole demo walks at the entrance floor's Y.
        float GroundHeight(float x, float z) => _groundY;

        public override void OnUpdate(float dt)
        {
            if (Manager!.Input.WasPressed(Key.Escape)) { Manager!.Pop(); return; }

            _character.Update(Manager!.Input, dt, _camera.Yaw, GroundHeight);

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
            scene.Draw(_capsule, Matrix4x4.CreateTranslation(p.X, footY, p.Z), new Color(0.85f, 0.55f, 0.25f, 1f));
        }

        // Tears down everything OnEnter built into the shared Scene3D, so the menu/other rooms render cleanly
        // and a re-entry rebuilds from scratch, matching Room3D's teardown.
        public override void OnExit()
        {
            if (!_built) return;
            _built = false;

            _scene.UnloadMesh(_capsule);
            foreach (MeshHandle h in _kitMeshes.Values) _scene.UnloadMesh(h);
            _kitMeshes.Clear();
            _placements = null!;

            _scene.CameraOverride = null;
        }
    }
}
