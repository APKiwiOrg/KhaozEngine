using System;
using System.Collections.Generic;
using KhaozEngine.Game;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;

namespace KhaozEngine.Showcase
{
    /// <summary>The showcase host: a GameApp3D holding a SceneManager and the room registry. Each room is a
    /// (display name, factory) pair. MenuScene lists them and pushes the chosen one. Later rooms self-register
    /// in OnLoad. Honors KE_MAX_FRAMES via the AppWindow loop (headless smoke renders N frames then exits 0).</summary>
    public sealed class ShowcaseApp : GameApp3D
    {
        readonly SceneManager _scenes = new();

        /// <summary>Room registry, in menu order. Rooms append here in OnLoad.</summary>
        public readonly List<(string Name, Func<GameScene> Factory)> Rooms = new();

        Texture2D _white = null!;

        public ShowcaseApp() : base(GameAppOptions.For("KhaozEngine Showcase", 1024, 640)) { }

        protected override void OnLoad()
        {
            _white = Surface2D.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);

            // Room2D's texture/fonts are created here (Surface2D is only reachable from the app, not a
            // GameScene) and wired into the room right after construction, so Room2D itself keeps a public
            // parameterless constructor for the Func<GameScene> factory below.
            var checker = Surface2D.CreateTexture(Room2D.Checker(64), 64, 64);
            var big = Surface2D.LoadDefaultFont(40f);
            var small = Surface2D.LoadDefaultFont(22f);
            Rooms.Add(("2D sprites + text", () => new Room2D().Init(_white, checker, big, small)));

            // RoomGui reuses the same big/small fonts (its own GuiAssets just wraps them alongside the white
            // texture) - no new Surface2D calls needed beyond what Room2D already created above.
            Rooms.Add(("GUI + widgets", () => new RoomGui().Init(_white, big, small)));

            // RoomInput reuses the shared white texture and the small font for its HUD text.
            Rooms.Add(("Input + audio", () => new RoomInput().Init(_white, small)));

            // RoomMiniGame reuses the same big/small fonts as its title/HUD text (no new Surface2D calls needed).
            Rooms.Add(("Mini-game (Catcher)", () => new RoomMiniGame().Init(_white, big, small)));

            // Room3D is the walkable streamed 3D overworld ported from TerrainWalkSample. It renders through
            // the app's shared Scene3D (injected here, since a GameScene cannot reach Surface3D itself).
            Rooms.Add(("3D World (walk)", () => new Room3D().Init(Scene, _white, small)));

            _scenes.Push(new MenuScene(_white, big, small, Rooms));

            // Smoke aid: KE_SHOWCASE_ROOM=<name> auto-enters that room, so a headless KE_MAX_FRAMES run actually
            // exercises the room's OnEnter/OnUpdate/OnDraw (the menu alone never builds a room's world). The push
            // is deferred to the first OnUpdate (below), not done here, because a room's OnEnter reads the scene
            // manager's Viewport/FrameWidth, which are only set once per frame in OnUpdate. Case-insensitive prefix
            // match on the display name, so "3D" or "mini" is enough. Unmatched = ignored.
            _autoRoom = System.Environment.GetEnvironmentVariable("KE_SHOWCASE_ROOM");
        }

        string? _autoRoom;

        protected override void OnUpdate(float dt)
        {
            _scenes.Input = Input;
            _scenes.Pointer = Pointer;
            _scenes.Viewport = Viewport;
            _scenes.FrameWidth = FrameWidth;
            _scenes.FrameHeight = FrameHeight;

            // Deferred auto-enter (see OnLoad): now that Viewport/FrameWidth are set, it is safe to enter a room.
            if (!string.IsNullOrWhiteSpace(_autoRoom))
            {
                string want = _autoRoom;
                _autoRoom = null;
                foreach (var r in Rooms)
                    if (r.Name.StartsWith(want, System.StringComparison.OrdinalIgnoreCase))
                    { _scenes.Push(r.Factory()); break; }
            }

            _scenes.Update(dt);
        }

        protected override void OnDraw2D(SpriteBatch batch) => _scenes.Draw2D(batch);
        protected override void OnDraw3D(Scene3D scene) => _scenes.Draw3D(scene);
        protected override void OnResize(int w, int h) => _scenes.Resize(w, h);
    }
}
