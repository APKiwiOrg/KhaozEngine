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

        /// <summary>Points of bottom-edge clearance the F7-F10 display readout band occupies, so a room can reserve
        /// it (the map editor's <see cref="KhaozEngine.MapEditor.MapEditorOptions.StatusBottomOffset"/>) and not
        /// draw its own chrome under the readout line.</summary>
        public const float DisplayReadoutHeight = 36f;

        /// <summary>Room registry, in menu order. Rooms append here in OnLoad.</summary>
        public readonly List<(string Name, Func<GameScene> Factory)> Rooms = new();

        Texture2D _white = null!;
        DpiFont _readoutFont = null!;   // point-space readout: baked at the live DPI scale so the overlay stays crisp

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

            // Two font families, both crisp on HiDPI:
            //  - DpiFonts (baked at the live DPI scale, drawn 1:1) for everything that draws through the point-space
            //    UI pass: the menu, the display readout, and the rooms whose text/HUD is a point-space overlay.
            //  - Oversample-3 SpriteFonts for the two ScreenStack rooms (RoomGui / RoomMiniGame): those stay in the
            //    design-space pass (their widget layout is a fixed centered canvas that must not reflow on resize),
            //    and a 3x supersampled atlas is minified by the design->framebuffer scale, so their text is crisp too.
            var dpi40 = Surface2D.LoadDefaultDpiFont(40f);
            var dpi22 = Surface2D.LoadDefaultDpiFont(22f);
            var big3 = Surface2D.LoadDefaultFont(40f, oversample: 3);
            var small3 = Surface2D.LoadDefaultFont(22f, oversample: 3);
            _readoutFont = dpi22; // crisp point-space overlay text

            // Room2D / RoomInput are fixed-layout design-space demos (they scale + centre as a unit on resize):
            // crisp via the supersampled fonts, same as the ScreenStack rooms below.
            Rooms.Add(("2D sprites + text", () => new Room2D().Init(_white, checker, big3, small3)));

            // RoomGui / RoomMiniGame are ScreenStack widget rooms: crisp via the supersampled design-space fonts.
            Rooms.Add(("GUI + widgets", () => new RoomGui().Init(_white, big3, small3)));

            Rooms.Add(("Input + audio", () => new RoomInput().Init(_white, small3)));

            Rooms.Add(("Mini-game (Catcher)", () => new RoomMiniGame().Init(_white, big3, small3)));

            // Room3D is the walkable streamed 3D overworld ported from TerrainWalkSample. It renders through
            // the app's shared Scene3D (injected here, since a GameScene cannot reach Surface3D itself); its
            // collision legend overlay draws crisp through the point-space UI pass.
            Rooms.Add(("3D World (walk)", () => new Room3D().Init(Scene, _white, dpi22)));

            // RoomNet is the networked-walk room: authoritative WorldServer + local WorldClient over loopback
            // UDP, demonstrating predict/replicate/reconcile netcode. Reuses the same shared Scene3D as Room3D.
            Rooms.Add(("Networked walk", () => new RoomNet().Init(Scene, _white, dpi22)));

            // Map editor: the turn-key KhaozEngine.MapEditor scene, registered directly (see RoomMapEditor's
            // doc comment for why a wrapper GameScene would leave it half-wired) over the committed showcase
            // demo document. Unlike the other rooms, plain Esc does NOT leave this room (the editor reserves
            // it for cancelling gizmo/draw gestures): Shift+Esc is the exit chord, with a discard warning
            // when there are unsaved changes. The manual verification room for Task 8: fly around, place a
            // prop, drag it with the gizmo, undo/redo, bake a scatter region, save, Shift+Esc back to menu.
            Rooms.Add(("Map editor", () => RoomMapEditor.Create(Scene, _white, dpi22)));

            // The landing menu draws through the point-space UI pass with the shared DpiFonts.
            _scenes.Push(new MenuScene(_white, dpi40, dpi22, Rooms));

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
            _scenes.UiViewport = Ui;
            _scenes.UiPointer = UiPointer;
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
            // The shared 3D scene is composed behind the 2D every frame; for a non-3D room it is empty and would show
            // through wherever the room's 2D (and the letterbox bars) do not paint. Lay an opaque backdrop over the
            // whole framebuffer first - an oversized design-space quad, so it covers the letterbox bars too - so 2D
            // rooms read on a clean background. A 3D room fills the window itself, so the backdrop is skipped there.
            if (_scenes.Active is not IGameScene3D)
            {
                Rect db = Viewport.DesignBounds;
                batch.Draw(_white, new Vector4(-db.Width, -db.Height, db.Width * 3f, db.Height * 3f), ClearColor);
            }
            _scenes.Draw2D(batch);
        }

        // The point-space UI pass (crisp on HiDPI): the scenes' own OnDrawUi (the menu), then the app-level display
        // readout overlay on top.
        protected override void OnDrawUi(SpriteBatch batch)
        {
            _scenes.DrawUi(batch);
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
        /// the bottom, so a windowed run visibly confirms each live change (and any tearing when vsync is off). Drawn
        /// in the point-space UI pass: positioned on the point-space bounds and baked at the DPI scale, so it stays
        /// put on resize and reads crisp on HiDPI.</summary>
        void DrawDisplayReadout(SpriteBatch batch)
        {
            DisplaySettings d = Display.CurrentDisplay;
            string cap = d.FrameCapHz > 0 ? $"{d.FrameCapHz}Hz" : "uncapped";
            string line = $"F7 vsync:{d.PresentMode}  F8 cap:{cap}  F9 mode:{d.WindowMode}  F10 res:{d.Width}x{d.Height}" +
                          $"   [fb {Window.FramebufferWidth}x{Window.FramebufferHeight}  {Backend}]";
            SpriteFont font = _readoutFont.For(Ui.DpiScale);
            // The readout occupies the bottom DisplayReadoutHeight band. A room that reserves it (the map editor,
            // via StatusBottomOffset) sits its own chrome directly above bandTop so nothing stacks on these pixels.
            float bandTop = Ui.Height - DisplayReadoutHeight;
            batch.Draw(_white, new Vector4(0, bandTop, Ui.Width, DisplayReadoutHeight - 4f), new Color(0f, 0f, 0f, 0.55f));
            batch.DrawString(font, line, new Vector2(16f, bandTop + 6f), new Color(0.85f, 0.95f, 1f, 1f));
        }
    }
}
