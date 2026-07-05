using System;
using System.Collections.Generic;
using System.Numerics;
using System.Resources;
using KhaozEngine.App;
using KhaozEngine.Game;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

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
        SpriteFont _hud = null!;

        // Runtime display-settings smoke controls (F7-F10), driven through the GameApp.Display surface. The cap /
        // resolution cycles walk fixed tables; window mode + present mode toggle. Overlaid state is drawn each frame.
        static readonly int[] CapCycle = { 0, 30, 60, 120 };
        static readonly (int W, int H)[] ResCycle = { (1024, 640), (1280, 720), (1600, 900) };
        int _resIndex;

        public ShowcaseApp() : base(BuildOptions()) { }

        // Window/taskbar icon on Windows/Linux and (via GameApp's macOS wiring) the Cocoa Dock icon, from the
        // committed assets/icon.png. Resolved against the runtime output dir so it works from any launch cwd.
        static GameAppOptions BuildOptions()
        {
            var options = GameAppOptions.For("KhaozEngine Showcase", 1024, 640);
            options.WindowIconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "assets", "icon.png");
            return options;
        }

        protected override void OnLoad()
        {
            // Compile-time localization: register the showcase string catalog so every LocalizedText resolves
            // against ShowcaseStrings.resx. See ShowcaseStrings.cs for the StringId constants the Gui sinks take.
            LocalizationContext.Catalog = new ResourceStringCatalog(
                new ResourceManager("KhaozEngine.Showcase.ShowcaseStrings", typeof(ShowcaseApp).Assembly));

            _white = Surface2D.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);

            // Room2D's texture/fonts are created here (Surface2D is only reachable from the app, not a
            // GameScene) and wired into the room right after construction, so Room2D itself keeps a public
            // parameterless constructor for the Func<GameScene> factory below.
            var checker = Surface2D.CreateTexture(Room2D.Checker(64), 64, 64);
            var big = Surface2D.LoadDefaultFont(40f);
            var small = Surface2D.LoadDefaultFont(22f);
            _hud = small; // reused for the display-settings readout overlay
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

            // RoomNet is the networked-walk room: authoritative WorldServer + local WorldClient over loopback
            // UDP, demonstrating predict/replicate/reconcile netcode. Reuses the same shared Scene3D as Room3D.
            Rooms.Add(("Networked walk", () => new RoomNet().Init(Scene, _white, small)));

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
            HandleDisplayKeys();

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

        protected override void OnDraw2D(SpriteBatch batch)
        {
            _scenes.Draw2D(batch);
            DrawDisplayReadout(batch);
        }

        protected override void OnDraw3D(Scene3D scene) => _scenes.Draw3D(scene);
        protected override void OnResize(int w, int h) => _scenes.Resize(w, h);

        /// <summary>Runtime display-settings smoke: F7 toggles vsync, F8 cycles the frame cap, F9 cycles the window
        /// mode, F10 cycles the windowed resolution - all through <see cref="GameApp.Display"/>, live and mid-frame.
        /// Handled at the app level so it works from the menu and any room.</summary>
        void HandleDisplayKeys()
        {
            if (Input.WasPressed(Key.F7))
                PresentMode = PresentMode == PresentMode.Vsync ? PresentMode.Immediate : PresentMode.Vsync;

            if (Input.WasPressed(Key.F8))
            {
                int i = Array.IndexOf(CapCycle, FrameCapHz);
                FrameCapHz = CapCycle[(i + 1 + CapCycle.Length) % CapCycle.Length];
            }

            if (Input.WasPressed(Key.F9))
                WindowMode = WindowMode switch
                {
                    WindowMode.Windowed => WindowMode.BorderlessFullscreen,
                    WindowMode.BorderlessFullscreen => WindowMode.ExclusiveFullscreen,
                    _ => WindowMode.Windowed,
                };

            if (Input.WasPressed(Key.F10))
            {
                _resIndex = (_resIndex + 1) % ResCycle.Length;
                var (w, h) = ResCycle[_resIndex];
                Display.Resize(w, h); // applies now in windowed mode; stored as the restore size in fullscreen
            }
        }

        /// <summary>Draw the current <see cref="DisplaySettings"/> plus the framebuffer size and backend as a bar at
        /// the bottom, so a windowed run visibly confirms each live change (and any tearing when vsync is off).</summary>
        void DrawDisplayReadout(SpriteBatch batch)
        {
            DisplaySettings d = Display.CurrentDisplay;
            string cap = d.FrameCapHz > 0 ? $"{d.FrameCapHz}Hz" : "uncapped";
            string line = $"F7 vsync:{d.PresentMode}  F8 cap:{cap}  F9 mode:{d.WindowMode}  F10 res:{d.Width}x{d.Height}" +
                          $"   [fb {Window.FramebufferWidth}x{Window.FramebufferHeight}  {Backend}]";
            // Position on the design bounds, not FrameWidth/FrameHeight (those are window pixels and drift on resize).
            Rect db = Viewport.DesignBounds;
            float y = db.Height - 30f;
            batch.Draw(_white, new Vector4(0, y - 6f, db.Width, 32f), new Color(0f, 0f, 0f, 0.55f));
            batch.DrawString(_hud, line, new Vector2(16f, y), new Color(0.85f, 0.95f, 1f, 1f));
        }
    }
}
