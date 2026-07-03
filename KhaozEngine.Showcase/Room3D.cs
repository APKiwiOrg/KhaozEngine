using KhaozEngine.Game;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>The walkable streamed 3D overworld, ported from TerrainWalkSample into a room. Renders through the
    /// showcase's shared Scene3D (injected via Init, since a GameScene cannot reach the app's 3D surface). Builds
    /// its world in OnEnter and tears it down in OnExit (the Scene3D is shared with the other rooms, so it must
    /// leave no camera override or loaded ring behind). Esc returns to the menu.</summary>
    public sealed class Room3D : GameScene, IGameScene3D
    {
        Scene3D _scene = null!;
        Texture2D _white = null!;
        SpriteFont _hud = null!;

        public Room3D Init(Scene3D scene, Texture2D white, SpriteFont hud)
        {
            _scene = scene; _white = white; _hud = hud;
            return this;
        }

        public override void OnEnter()
        {
            // Task 3+: build terrain/streaming/camera/physics/character/props into _scene here.
        }

        public override void OnUpdate(float dt)
        {
            if (Manager!.Input.WasPressed(Key.Escape)) { Manager!.Pop(); return; }
            // Task 3+: physics + character + streamer + camera + toggles here.
        }

        public void OnDraw3D(Scene3D scene)
        {
            // Task 3+: chunk sink + props + character + overlay here.
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
