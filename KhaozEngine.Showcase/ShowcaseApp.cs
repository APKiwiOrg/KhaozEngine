using System;
using System.Collections.Generic;
using KhaozEngine.Game;
using KhaozEngine.Render2D;

namespace KhaozEngine.Showcase
{
    /// <summary>The showcase host: a GameApp holding a SceneManager and the room registry. Each room is a
    /// (display name, factory) pair; MenuScene lists them and pushes the chosen one. Later rooms self-register
    /// in OnLoad. Honors KE_MAX_FRAMES via the AppWindow loop (headless smoke renders N frames then exits 0).</summary>
    public sealed class ShowcaseApp : GameApp
    {
        readonly SceneManager _scenes = new();

        /// <summary>Room registry, in menu order. Rooms append here in OnLoad.</summary>
        public readonly List<(string Name, Func<GameScene> Factory)> Rooms = new();

        Texture2D _white = null!;

        public ShowcaseApp() : base(GameAppOptions.For("KhaozEngine Showcase", 1024, 640)) { }

        protected override void OnLoad()
        {
            _white = Surface2D.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
            // Rooms are registered here (added by later tasks), e.g.:
            //   Rooms.Add(("2D", () => new Room2D()));
            _scenes.Push(new MenuScene(_white, Rooms));
        }

        protected override void OnUpdate(float dt)
        {
            _scenes.Input = Input;
            _scenes.Pointer = Pointer;
            _scenes.Viewport = Viewport;
            _scenes.FrameWidth = FrameWidth;
            _scenes.FrameHeight = FrameHeight;
            _scenes.Update(dt);
        }

        protected override void OnDraw2D(SpriteBatch batch) => _scenes.Draw2D(batch);
        protected override void OnResize(int w, int h) => _scenes.Resize(w, h);
    }
}
