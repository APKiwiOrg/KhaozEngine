using System;
using System.Collections.Generic;
using System.Numerics;
using System.Resources;
using KhaozEngine.App;
using KhaozEngine.Diagnostics;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>One entry in the showcase room registry: the localized tile <paramref name="Title"/> and one-line
    /// <paramref name="Blurb"/> the hub menu shows, plus the <paramref name="Factory"/> that builds the room scene
    /// when it is entered. The title also drives the KE_SHOWCASE_ROOM auto-enter, a case-insensitive prefix match
    /// against the resolved title.</summary>
    public sealed record ShowcaseRoomEntry(StringId Title, StringId Blurb, Func<GameScene> Factory);

    /// <summary>The showcase host: a GameApp3D holding a SceneManager, the room registry, and the shared
    /// <see cref="ShowcaseHud"/> that draws each room's chrome (title / controls hint / status / toasts). Each
    /// registry entry pairs a localized title + blurb with a scene factory, and <see cref="MenuScene"/> lays them
    /// out as a tile grid and pushes the chosen one. Honors KE_MAX_FRAMES via the AppWindow loop (headless smoke
    /// renders N frames then exits 0) and KE_SHOWCASE_ROOM to auto-enter a room by title prefix.</summary>
    public sealed class ShowcaseApp : GameApp3D
    {
        readonly SceneManager _scenes = new();

        /// <summary>Points of bottom-edge clearance the F7-F10 display readout band occupies, so a room can reserve
        /// it (the map editor's <see cref="KhaozEngine.MapEditor.MapEditorOptions.StatusBottomOffset"/>) and not
        /// draw its own chrome under the readout line. The shared <see cref="ShowcaseHud"/> also sits its controls
        /// band directly above this band.</summary>
        public const float DisplayReadoutHeight = 36f;

        /// <summary>Room registry, in menu order. Rooms are appended here in OnLoad.</summary>
        public readonly List<ShowcaseRoomEntry> Rooms = new();

        Texture2D _white = null!;
        DpiFont _readoutFont = null!;   // point-space readout: baked at the live DPI scale so the overlay stays crisp
        ShowcaseHud _hud = null!;       // shared room chrome (title band, controls band, status line, toasts)

        // Env-gated field capture (KE_TELEMETRY_PATH). Unarmed and free when the variable is unset. See
        // ShowcaseTelemetry for why the testbed carries one and why it widens no engine API.
        readonly ShowcaseTelemetry _telemetry = new();

        // Runtime display-settings smoke controls (F7-F10), driven through the GameApp.Display surface. The cap /
        // resolution cycles walk fixed tables; window mode + present mode toggle. Overlaid state is drawn each frame.
        static readonly int[] CapCycle = { 0, 30, 60, 120 };
        static readonly (int W, int H)[] ResCycle = { (1024, 640), (1280, 720), (1600, 900) };
        int _resIndex;

        public ShowcaseApp() : base(BuildOptions()) { }

        /// <summary>The session-log bootstrap the head hands to <see cref="SessionLog.Configure(SessionLogOptions)"/>
        /// before the window opens. It is a method rather than three lines in Program.cs so the contract is
        /// readable without a window: the showcase is the engine's windowed testbed, and a one-off unhandled
        /// managed exception here used to reach only the terminal that launched it, which is exactly how #607
        /// lost the one crash it had. With the crash handler armed, the same exception lands in the session log
        /// the tester still has after the run.</summary>
        public static SessionLogOptions BootLogOptions() => new()
        {
            Directory = new AppDataPaths("APKiwi", "KhaozEngine.Showcase").GetFilePath("logs"),
            ProcessLabel = "KhaozEngine.Showcase",
            InstallCrashHandler = true,
        };

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

            // Room2DGui's textures are created here (Surface2D is only reachable from the app, not a GameScene) and
            // handed in via Init: the checker sprite for its sprites page and the procedural nine-slice frame skin
            // for the skinned-chrome widgets on its widgets page.
            var checker = Surface2D.CreateTexture(Room2DGui.Checker(64), 64, 64);
            var frameTex = Surface2D.CreateTexture(
                Room2DGui.BakeFramePixels(Room2DGui.FrameSize, Room2DGui.FrameInset), Room2DGui.FrameSize, Room2DGui.FrameSize);
            var guiSkin = GuiSkin.NineSlice(frameTex, Room2DGui.FrameInset);

            // Two font families, both crisp on HiDPI:
            //  - DpiFonts (baked at the live DPI scale, drawn 1:1) for everything drawn through the point-space UI
            //    pass: the menu (dpi40 title / dpi22 tile titles / dpi16 subtitle-blurb-hint-footer), the shared
            //    hud chrome (dpi22 title + dpi16 body), the display readout, and the 3D rooms' point-space overlays.
            //  - Oversample-3 SpriteFonts for the design-space ScreenStack rooms (Room2DGui / RoomMiniGame): their
            //    widget layout is a fixed centred canvas that must not reflow on resize, and a 3x supersampled atlas
            //    minified by the design->framebuffer scale keeps that text crisp too.
            var dpi40 = Surface2D.LoadDefaultDpiFont(40f);
            var dpi22 = Surface2D.LoadDefaultDpiFont(22f);
            var dpi16 = Surface2D.LoadDefaultDpiFont(16f);
            var big3 = Surface2D.LoadDefaultFont(40f, oversample: 3);
            var small3 = Surface2D.LoadDefaultFont(22f, oversample: 3);
            var bootFont = Surface2D.LoadDefaultDpiFont(28f, cacheSlots: 4);   // DPI-aware: crisp boot text on HiDPI
            _readoutFont = dpi22; // crisp point-space overlay text

            // The shared room chrome: title band + status line in dpi22/dpi16, controls band + toasts. Rooms that
            // toast (Room3D / RoomDungeon toggles) receive it via Init.
            _hud = new ShowcaseHud(_white, dpi22, dpi16);

            // Room2DGui folds the old 2D-sprites, GUI-widgets, and input-audio rooms into one tabbed toolkit tour
            // (a ScreenStack with a tab host + five pages), crisp via the supersampled design-space fonts.
            Rooms.Add(new ShowcaseRoomEntry(ShowcaseStrings.RoomGui2DTitle, ShowcaseStrings.RoomGui2DBlurb,
                () => new Room2DGui().Init(_white, checker, big3, small3, guiSkin)));

            // RoomMiniGame is a ScreenStack widget room: crisp via the supersampled design-space fonts.
            Rooms.Add(new ShowcaseRoomEntry(ShowcaseStrings.RoomMiniGameTitle, ShowcaseStrings.RoomMiniGameBlurb,
                () => new RoomMiniGame().Init(_white, big3, small3)));

            // Boot screen: the turn-key startup pipeline (KhaozEngine.Game.Boot) driven with fake delayed steps, so
            // the instant-on bar + staged progress + error/retry state can be seen without a real update feed.
            Rooms.Add(new ShowcaseRoomEntry(ShowcaseStrings.RoomBootTitle, ShowcaseStrings.RoomBootBlurb,
                () => new RoomBoot().Init(_white, bootFont)));

            // Room3D is the walkable streamed 3D overworld. It renders through the app's shared Scene3D (injected
            // here, since a GameScene cannot reach Surface3D itself). Its overlays draw crisp through the UI pass,
            // and its render/skinning toggles toast through the shared hud.
            Rooms.Add(new ShowcaseRoomEntry(ShowcaseStrings.RoomWorldTitle, ShowcaseStrings.RoomWorldBlurb,
                () => new Room3D().Init(Scene, _white, dpi22, _hud)));

            // RoomVfx is the particles + modern VFX room: every authored VfxPresets effect played through a
            // ParticleEffectPlayer and drawn with Render3D's modern particle pass (bloom on by default), on the same
            // shared Scene3D as Room3D. Its bloom / HDR toggles toast through the shared hud.
            Rooms.Add(new ShowcaseRoomEntry(ShowcaseStrings.RoomVfxTitle, ShowcaseStrings.RoomVfxBlurb,
                () => new RoomVfx().Init(Scene, _hud)));

            // RoomNet is the networked-walk room: authoritative WorldServer + local WorldClient over loopback UDP,
            // demonstrating predict/replicate/reconcile netcode. Reuses the same shared Scene3D as Room3D. Its live
            // net stats surface through the chrome status line (no toggles, so no hud toasts needed).
            Rooms.Add(new ShowcaseRoomEntry(ShowcaseStrings.RoomNetTitle, ShowcaseStrings.RoomNetBlurb,
                () => new RoomNet().Init(Scene)));

            // RoomDungeon is the walkable dungeon-generator demo: DungeonGenerator + DungeonStamp over the greybox
            // kit, rendered as instanced props through the same shared Scene3D as Room3D. Its outline toggle toasts.
            Rooms.Add(new ShowcaseRoomEntry(ShowcaseStrings.RoomDungeonTitle, ShowcaseStrings.RoomDungeonBlurb,
                () => new RoomDungeon().Init(Scene, _hud)));

            // Map editor: the turn-key KhaozEngine.MapEditor scene, registered directly (see RoomMapEditor's doc
            // comment for why a wrapper GameScene would leave it half-wired) over the committed showcase demo
            // document. Unlike the other rooms, plain Esc does NOT leave this room (the editor reserves it for
            // cancelling gizmo/draw gestures): Shift+Esc is the exit chord, with a discard warning when there are
            // unsaved changes. It is not an IShowcaseRoom (it carries its own chrome), so the shared hud skips it.
            Rooms.Add(new ShowcaseRoomEntry(ShowcaseStrings.RoomMapEditorTitle, ShowcaseStrings.RoomMapEditorBlurb,
                () => RoomMapEditor.Create(Scene, _white, dpi22)));

            // The landing menu draws through the point-space UI pass with the shared DpiFonts.
            _scenes.Push(new MenuScene(_white, dpi40, dpi22, dpi16, Rooms));

            // Feed the built-in diagnostics HUD (F1) a Network section whenever the networked-walk room is active:
            // the source returns the live client stats there and null everywhere else (so the section drops out).
            Diagnostics?.SetNetStatsSource(() => (_scenes.Active as RoomNet)?.NetStats);

            // Smoke aid: KE_SHOWCASE_ROOM=<name> auto-enters that room, so a headless KE_MAX_FRAMES run actually
            // exercises the room's OnEnter/OnUpdate/OnDraw (the menu alone never builds a room's world). The push
            // is deferred to the first OnUpdate (below), not done here, because a room's OnEnter reads the scene
            // manager's Viewport/FrameWidth, which are only set once per frame in OnUpdate. Case-insensitive prefix
            // match on the resolved room title, so "3D" or "mini" is enough. Unmatched = ignored.
            _autoRoom = System.Environment.GetEnvironmentVariable("KE_SHOWCASE_ROOM");

            // KE_SHOWCASE_FRAME_CAP pins the frame-cap intent for a measured run. The default Auto resolves to a
            // SOFTWARE cap on the INCUMBENT Metal backend plus vsync (FrameCap.Resolve), and that cap slept the
            // loop before the present ever blocked. A capture taken under it therefore reads the acquire-wait
            // counters as near zero no matter how the backend acquires, which is a false pass rather than a
            // reading. MM4's exit criterion is stated against the UNCAPPED capture for exactly that reason, so the
            // run that reads it has to be able to ask for one. It still has to: gate 5 measured MetalNative's
            // present as throttling from vsync and took it OUT of the capped arm (M-W3), so this lever is what an
            // incumbent baseline capture needed, and what pinned the two runs to the same pacing on both backends.
            if (ParseFrameCap(System.Environment.GetEnvironmentVariable("KE_SHOWCASE_FRAME_CAP")) is { } cap)
                FrameCap = cap;

            // KE_SHOWCASE_BACKGROUND_THROTTLE=off disables the unfocused/minimized pacing for a measured run.
            // The engine default caps an unfocused-but-visible window at BackgroundThrottlePolicy.DefaultUnfocusedHz
            // (15 Hz), which is correct for a game and ruinous for an unattended capture: an 80-second windowed run
            // that never took focus reads a flat 66.67 ms frame time and looks like a backend that collapsed. That
            // is not hypothetical, it is what the first gate 4 baseline attempt recorded before this existed. Focus
            // is not a variable a reproducible capture (or gate 4's week-long soak) can afford to carry.
            if (ParseDisabled(System.Environment.GetEnvironmentVariable("KE_SHOWCASE_BACKGROUND_THROTTLE")))
                BackgroundThrottle = BackgroundThrottlePolicy.Disabled;

            // Arm the field capture last, once the window and its device exist, so the session header records the
            // backend that actually came up rather than the one that was asked for.
            _telemetry.Start(Window);
        }

        /// <summary>Parse the KE_SHOWCASE_FRAME_CAP value: <c>auto</c>, <c>uncapped</c> (or <c>0</c>), or a positive
        /// integer Hz. Null (leave the default alone) for unset, blank, or anything unrecognized, so a typo cannot
        /// silently change the pacing of a measured run into something nobody chose. Pure.</summary>
        internal static FrameCap? ParseFrameCap(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            string text = value.Trim();
            if (string.Equals(text, "auto", StringComparison.OrdinalIgnoreCase)) return FrameCap.Auto;
            if (string.Equals(text, "uncapped", StringComparison.OrdinalIgnoreCase)) return FrameCap.Uncapped;
            if (!int.TryParse(text, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int hz)) return null;

            return hz > 0 ? FrameCap.Hz(hz) : FrameCap.Uncapped;
        }

        /// <summary>True when <paramref name="value"/> asks for a feature to be turned off (<c>off</c>, <c>0</c>,
        /// <c>false</c>, <c>no</c>, case-insensitive). Anything else, including unset, leaves the default. Pure.</summary>
        internal static bool ParseDisabled(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            string text = value.Trim();
            return string.Equals(text, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "0", StringComparison.Ordinal)
                || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "no", StringComparison.OrdinalIgnoreCase);
        }

        string? _autoRoom;

        protected override void OnUpdate(float dt)
        {
            // Metered from the RAW frame delta, not the dt handed in here (which is time-scaled), so a capture
            // measures the machine. No-op unless KE_TELEMETRY_PATH armed it.
            _telemetry.Sample(Clock.RealDeltaSeconds, Clock.ElapsedRealSeconds, Window);

            HandleDisplayKeys();
            _hud.Update(dt);

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
                foreach (ShowcaseRoomEntry r in Rooms)
                {
                    string title = ((LocalizedText)r.Title).Resolve();
                    if (title.StartsWith(want, System.StringComparison.OrdinalIgnoreCase))
                    { _scenes.Push(r.Factory()); break; }
                }
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

        // The point-space UI pass (crisp on HiDPI): the scenes' own OnDrawUi, then the shared room chrome, then the
        // app-level display readout overlay on top. The chrome reads the active scene as an IShowcaseRoom (null for
        // the menu / map editor, which carry their own chrome).
        protected override void OnDrawUi(SpriteBatch batch)
        {
            _scenes.DrawUi(batch);
            _hud.Draw(batch, Ui, _scenes.Active as IShowcaseRoom);
            DrawDisplayReadout(batch);
        }

        protected override void OnDraw3D(Scene3D scene) => _scenes.Draw3D(scene);
        protected override void OnResize(int w, int h) => _scenes.Resize(w, h);

        /// <summary>Flush and close the field capture, so a KE_MAX_FRAMES run leaves a complete file behind (a
        /// crash still leaves a valid partial one, since the recorder flushes every row).</summary>
        protected override void OnDispose() => _telemetry.Dispose();

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
