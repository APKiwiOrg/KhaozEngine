using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
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

        Scene3D _scene = null!;
        Texture2D _white = null!;
        SpriteFont _hud = null!;

        TerrainField _field = null!;
        TerrainCollision _terrain = null!;
        FollowCamera3D _camera = null!;
        FollowCameraController _camController = null!;

        // No character yet (Task 4): an empty prop-mesh set and no physics world, so the sink scatters nothing and
        // adds no collision statics. Props + physics arrive in Tasks 4-5.
        readonly Dictionary<string, MeshHandle> _propMeshes = new();

        Scene3DChunkSink _sink = null!;
        TerrainStreamer _streamer = null!;

        public Room3D Init(Scene3D scene, Texture2D white, SpriteFont hud)
        {
            _scene = scene; _white = white; _hud = hud;
            return this;
        }

        public override void OnEnter()
        {
            _field = new TerrainField(TerrainPresets.BoundedClearing());
            _terrain = new TerrainCollision(_field);

            var terrainMaterial = _scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural());

            // No character yet, so seed the camera/streamer focus at the origin (Task 4 switches the target to the
            // character's position).
            _camera = new FollowCamera3D { Target = Vector3.Zero, HeightOffset = 1.2f, GroundHeight = _terrain.GroundHeight };
            _camera.Distance = 9f;
            _camController = new FollowCameraController(_camera);
            _scene.CameraOverride = _camera;

            _sink = new Scene3DChunkSink(_scene, _field, ScatterConfig.ForestRing(), _propMeshes,
                chunkSize: TerrainChunkRegion.DefaultSize, propDrawRadius: PropDrawRadius, material: terrainMaterial,
                physics: null, collisionShapes: null);
            _streamer = new TerrainStreamer(StreamerConfig.Default, _sink);

            // Prime the FULL initial ring at load time (this is the loading moment, not a frame, so the per-frame
            // MaxLoadsPerFrame budget is irrelevant here): pump until the loaded set stops growing.
            int loadedBefore = -1;
            while (_streamer.Loaded.Count != loadedBefore)
            {
                loadedBefore = _streamer.Loaded.Count;
                _streamer.Update(_camera.Target, 0f);
            }
        }

        public override void OnUpdate(float dt)
        {
            if (Manager!.Input.WasPressed(Key.Escape)) { Manager!.Pop(); return; }

            _camController.Update(Manager!.Input, dt);

            // No character yet: drive the streamer focus + camera target from the camera position (temporary;
            // Task 4 switches focus to the character).
            _streamer.Update(_camera.Target, dt);

            _camera.AspectRatio = Manager!.FrameHeight > 0 ? (float)Manager!.FrameWidth / Manager!.FrameHeight : _camera.AspectRatio;
        }

        public void OnDraw3D(Scene3D scene)
        {
            _sink.Draw(_camera.Target);
        }

        public override void OnDraw2D(SpriteBatch batch)
        {
            // Task 5: HUD here.
        }

        public override void OnExit()
        {
            // Task 6: dispose streamer + physics, clear _scene.CameraOverride, reset _scene.Post.
        }
    }
}
