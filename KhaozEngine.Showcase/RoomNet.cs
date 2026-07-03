using KhaozEngine.Game;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>Networked walk room: an authoritative WorldServer + a local WorldClient + scripted bot clients,
    /// all running in-process on the main thread over a loopback UDP socket. Demonstrates the predict / replicate /
    /// reconcile netcode against moving remote players without launching a separate server. Renders through the
    /// showcase's shared Scene3D (injected via Init). Esc returns to the menu.</summary>
    public sealed class RoomNet : GameScene, IGameScene3D
    {
        Scene3D _scene = null!;
        Texture2D _white = null!;
        SpriteFont _hud = null!;

        public RoomNet Init(Scene3D scene, Texture2D white, SpriteFont hud)
        {
            _scene = scene; _white = white; _hud = hud;
            return this;
        }

        public override void OnEnter() { /* Task 2+: terrain + server + client + bots */ }

        public override void OnUpdate(float dt)
        {
            if (Manager!.Input.WasPressed(Key.Escape)) { Manager!.Pop(); return; }
            // Task 2+: step server + clients here.
        }

        public void OnDraw3D(Scene3D scene) { /* Task 2+: terrain + characters */ }

        public override void OnDraw2D(SpriteBatch batch) { /* Task 5: net HUD */ }

        public override void OnExit() { /* Task 5/6: dispose clients + server + transports, free meshes */ }
    }
}
