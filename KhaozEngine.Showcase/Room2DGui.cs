using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Audio;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.Platform;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>The consolidated 2D and GUI toolkit tour, folding the old sprites / GUI-widgets / input-audio rooms
    /// into one <see cref="ScreenStack"/> whose single root screen (<see cref="ToolkitHostScreen"/>) carries a
    /// <see cref="TabBar"/> over five pages: widgets, sprites and text, input and audio, immediate-mode, and the
    /// screens-and-dialogs launcher. The modal demos (Settings, the pause overlay, the popup, patch notes) still
    /// push real <see cref="Screen"/>s on top of the tab host, which IS the screen-stack demo. Esc is centralized
    /// here exactly as the old GUI room did: the topmost screen exits first, and only once the stack is back to the
    /// root does Esc leave the room. The room also hosts the toast demo (launched from the Screens page): it owns
    /// the <see cref="ToastStack"/> model, ticked from <see cref="OnUpdate"/> with the raw frame dt, while the
    /// <see cref="ToastView"/> lives in a permanent <see cref="ToastOverlayScreen"/> inside <see cref="_stack"/>
    /// (topmost by DrawOrder, passthrough), so toasts stay visible and tappable over every page and modal. A
    /// <see cref="GameScene"/> cannot reach <c>Surface2D</c> itself, so <see cref="ShowcaseApp"/> creates the
    /// textures/fonts and hands them in via <see cref="Init"/> right after construction, keeping the constructor
    /// parameterless for the room registry's <c>Func&lt;GameScene&gt;</c>.</summary>
    public sealed class Room2DGui : GameScene, IShowcaseRoom
    {
        static readonly StringId[] Hints = { ShowcaseStrings.ControlsGui2D };

        GuiAssets _assets = null!;
        ScreenStack _stack = null!;
        ToastStack _toasts = null!;

        public StringId Title => ShowcaseStrings.RoomGui2DTitle;
        public IReadOnlyList<StringId> ControlsHints => Hints;

        /// <summary>Wire in the textures/fonts created on the app's Surface2D. Call once, right after construction
        /// and before the room is pushed. <paramref name="checker"/> feeds the sprites page, and <paramref name="skin"/>
        /// is the nine-slice frame skin the skinned-chrome widgets use (baked via <see cref="BakeFramePixels"/>).</summary>
        public Room2DGui Init(Texture2D white, Texture2D checker, SpriteFont big, SpriteFont small, GuiSkin skin)
        {
            _assets = new GuiAssets(white, checker, big, small, skin);
            return this;
        }

        /// <summary>Build the 64x64 checker pattern the Render2D sample used to prove textured sprites.</summary>
        public static byte[] Checker(int size)
        {
            var px = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool on = ((x / 8) + (y / 8)) % 2 == 0;
                    int i = (y * size + x) * 4;
                    byte r = on ? (byte)240 : (byte)200, g = on ? (byte)215 : (byte)100, b = on ? (byte)130 : (byte)60;
                    px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255;
                }
            return px;
        }

        /// <summary>Source pixel size / inset of the demo nine-slice frame texture (<see cref="BakeFramePixels"/>).</summary>
        public const int FrameSize = 48, FrameInset = 12;

        /// <summary>
        /// Bake an original, procedural CC0 nine-slice frame (a beveled gold border with bright corner studs over a
        /// translucent centre) into an RGBA8 pixel buffer, mirroring how <c>tools/TestModelGen</c> emits assets in
        /// code rather than shipping a file. Feed it to <c>Surface2D.CreateTexture</c> then
        /// <see cref="GuiSkin.NineSlice(Texture2D, float)"/> with <see cref="FrameInset"/>.
        /// </summary>
        public static byte[] BakeFramePixels(int size, int inset)
        {
            var px = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int e = Math.Min(Math.Min(x, y), Math.Min(size - 1 - x, size - 1 - y));   // dist to nearest edge
                    (byte r, byte g, byte b, byte a) c =
                        e >= inset ? ((byte)35, (byte)50, (byte)75, (byte)205)   // translucent centre
                        : e < 2 ? ((byte)25, (byte)20, (byte)15, (byte)255)      // dark rim
                        : e < 7 ? ((byte)210, (byte)170, (byte)70, (byte)255)    // gold
                        : ((byte)120, (byte)95, (byte)55, (byte)255);            // brown inner frame
                    if (Math.Min(x, size - 1 - x) < 6 && Math.Min(y, size - 1 - y) < 6)
                        c = (255, 240, 190, 255);                                // bright corner stud
                    int i = (y * size + x) * 4;
                    px[i] = c.r; px[i + 1] = c.g; px[i + 2] = c.b; px[i + 3] = c.a;
                }
            return px;
        }

        public override void OnEnter()
        {
            _assets.Vp = Manager!.Viewport!;   // design viewport the pages + modal screens lay out against
            _toasts = new ToastStack();
            _assets.Toasts = _toasts;          // the Screens page's toast demo fires into the room-owned stack
            _stack = new ScreenStack();
            _stack.Add(new ToolkitHostScreen(_assets));
            _stack.Add(new ToastOverlayScreen(_assets, _toasts));   // permanent, topmost
        }

        public override void OnUpdate(float dt)
        {
            var m = Manager!;

            // A real game feeds ToastStack.Update a RAW, unscaled frame dt (Frame.Dt / GameClock.RealDeltaSeconds),
            // never a scaled simulation dt, so toasts keep counting down at real speed while the game is paused or
            // slowed. The showcase has no time scaling of its own, so dt here already is that raw value. The tick
            // stays host-driven for that reason: ToastOverlayScreen only handles input and drawing.
            _toasts.Update(dt);

            // Esc backs out exactly one level. With a modal sub-screen open (Settings/Overlay/Popup/Patch notes/
            // Toasts), Esc exits the topmost demo screen (which plays its off-transition) and returns to the tab
            // host. Only once the stack is back down to the host does Esc leave the room via Manager.Pop().
            // Centralized here so no sub-screen needs its own Esc handler (avoids a double-pop on the same frame).
            // The permanent ToastOverlayScreen is always Screens[^1] (highest DrawOrder) and never counts as a
            // backable level, so the checks skip it: root = 2 screens, and the top demo screen is Screens[^2].
            if (m.Input.WasPressed(Key.Escape))
            {
                if (_stack.Screens.Count <= 2) { m.Pop(); return; }
                _stack.Screens[^2].ExitScreen();
                return;
            }
            _stack.Update(dt, m.Input, m.Viewport);
        }

        public override void OnDraw2D(SpriteBatch batch) => _stack.Draw(batch);

        // Remove every screen so each one's UnloadContent runs (the tab host's frees its pages' audio / GPU state),
        // even if a modal was still open when the room popped.
        public override void OnExit()
        {
            while (_stack.Screens.Count > 0) _stack.Remove(_stack.Screens[^1]);
        }
    }

    /// <summary>Shared white + checker textures, two font sizes, the demo nine-slice skin, and the design viewport
    /// the pages/modals lay out against (set on room enter). Mirrors the old <c>GuiSample</c> <c>GuiAssets</c>.</summary>
    sealed class GuiAssets
    {
        public readonly Texture2D White;
        public readonly Texture2D Checker;
        public readonly SpriteFont Big, Small;
        public readonly GuiSkin Skin;

        /// <summary>The design viewport pages and modal screens resolve their layout against. Set on room enter
        /// (a <see cref="GameScene"/> cannot reach it until it has a <c>Manager</c>).</summary>
        public IDesignViewport Vp = null!;

        /// <summary>The room-owned toast stack the Screens page's toast demo fires into. Set on room enter, beside
        /// <see cref="Vp"/>, since the stack's lifetime is one room visit.</summary>
        public ToastStack Toasts = null!;

        public GuiAssets(Texture2D white, Texture2D checker, SpriteFont big, SpriteFont small, GuiSkin skin)
        {
            White = white; Checker = checker; Big = big; Small = small; Skin = skin;
        }

        /// <summary>A button/panel/bar style backed by <see cref="Skin"/>: the resting fill is white (skin native),
        /// hover/press are light/dim tints multiplied over the sprite, and the label is the gold frame accent.</summary>
        public GuiStyle SkinStyle => new()
        {
            Skin = Skin,
            Fill = Vector4.One,
            Hover = new Vector4(0.82f, 0.88f, 1f, 1f),
            Press = new Vector4(0.62f, 0.68f, 0.8f, 1f),
            SelectedFill = new Vector4(0.9f, 0.95f, 1f, 1f),
            Border = Vector4.One,
            SelectedBorder = Vector4.One,
            Text = new Vector4(0.98f, 0.94f, 0.78f, 1f),
            DisabledFill = new Vector4(0.5f, 0.5f, 0.5f, 1f),
            DisabledText = new Vector4(0.65f, 0.65f, 0.65f, 1f),
        };
    }

    /// <summary>The room's single root screen: an opaque backdrop, a <see cref="TabBar"/> at the top, and one of
    /// five <see cref="ToolkitPage"/>s below it. Tab / Shift+Tab (or a click on the bar) switches pages, and the
    /// modal demos push real screens on top of this one.
    /// <para>
    /// Deliberately NOT a <see cref="ScreenComponentList"/>, even though every page is an
    /// <see cref="IScreenComponent"/>: the pages are mutually exclusive TABS, exactly one of which runs, and a
    /// fan-out list is for the many-at-once case. The list would update and draw all five. This is the useful
    /// half of the distinction: the interface is the per-component contract, the list is one collection over it,
    /// and a host with different collection semantics keeps its own array and still speaks the same contract.
    /// </para></summary>
    sealed class ToolkitHostScreen : Screen
    {
        readonly GuiAssets _a;
        TabBar _tabs = null!;
        ToolkitPage[] _pages = null!;
        bool[] _loaded = null!;
        int _active;

        public ToolkitHostScreen(GuiAssets a)
        {
            _a = a;
            PassUpdateThrough = false;
            BackgroundColor = GuiTheme.Default.Background;   // opaque full screen
        }

        public override void LoadContent()
        {
            // The bar starts at y 56 so the room's point-space title pill (34 points tall, top-left, drawn by the
            // shared chrome) clears it at every window scale: the design-to-point scale only shrinks below 1 on a
            // window smaller than the design size, where 56 design units still land below the pill's 40 points.
            Rect db = _a.Vp.DesignBounds;
            float barW = MathF.Min(920f, db.Width - 80f);
            _tabs = new TabBar(
                new LocalizedText[]
                {
                    ShowcaseStrings.TabWidgets, ShowcaseStrings.TabSprites, ShowcaseStrings.TabInput,
                    ShowcaseStrings.TabImmediate, ShowcaseStrings.TabScreens,
                },
                _a.Small, new Rect((db.Width - barW) * 0.5f, 56f, barW, 40f));

            _pages = new ToolkitPage[]
            {
                new WidgetsPage(), new SpritesTextPage(), new InputAudioPage(), new ImmediatePage(), new ScreensPage(),
            };
            _loaded = new bool[_pages.Length];

            _active = 0;
            _tabs.ActiveIndex = 0;
            EnsureLoaded(0);
            _pages[0].Activated();
        }

        public override bool Update(float dt, bool receivesInput)
        {
            if (!receivesInput) return false;   // a modal is on top: frozen (it owns input this frame)

            int prev = _tabs.ActiveIndex;
            _tabs.Update(Manager.Pointer);   // reserves the bar region (click-through gate) + handles tab clicks

            // Tab / Shift+Tab cycle the active tab, wrapping both ways (the setter does not raise ChangedThisFrame,
            // so the prev/next compare below drives the page swap for keyboard and click alike).
            if (Manager.Input.WasPressed(Key.Tab))
            {
                bool shift = Manager.Input.IsDown(Key.LeftShift) || Manager.Input.IsDown(Key.RightShift);
                int n = _tabs.Count;
                _tabs.ActiveIndex = shift ? (_tabs.ActiveIndex - 1 + n) % n : (_tabs.ActiveIndex + 1) % n;
            }

            if (_tabs.ActiveIndex != prev) SetActive(_tabs.ActiveIndex);
            // The page's own consumed flag is deliberately discarded: this screen is modal by construction
            // (PassUpdateThrough = false above), so it owns input whenever it is the top screen and nothing
            // below it can be starved by the answer either way.
            _ = _pages[_active].Update(dt, receivesInput, PageBounds(), Manager.InputManager);
            return true;
        }

        public override void Draw(SpriteBatch batch)
        {
            DrawBackground(batch, _a.White, _a.Vp);
            _pages[_active].Draw(batch, PageBounds());
            _tabs.Draw(batch, _a.White);   // crisp strip on top of the page
        }

        public override void UnloadContent()
        {
            for (int i = 0; i < _pages.Length; i++)
                if (_loaded[i]) _pages[i].UnloadContent();
        }

        /// <summary>The region a page lays out inside, resolved fresh every frame off the viewport and handed
        /// down as the <see cref="IScreenComponent"/> bounds argument rather than captured at load. That is what
        /// lets a page drop its resize handling entirely: whatever the viewport reports this frame is what it
        /// lays out against.</summary>
        Rect PageBounds()
        {
            Rect db = _a.Vp.DesignBounds;
            return new Rect((db.Width - 920f) * 0.5f, 112f, 920f, db.Height - 112f - 16f);
        }

        void SetActive(int index)
        {
            if (index == _active) return;
            _pages[_active].Deactivated();
            _active = index;
            EnsureLoaded(_active);
            _pages[_active].Activated();
        }

        void EnsureLoaded(int index)
        {
            if (_loaded[index]) return;
            _pages[index].Load(_a, Manager);
            _loaded[index] = true;
        }
    }

    /// <summary>Base for one tab page: an <see cref="IScreenComponent"/> laid out inside the bounds the host
    /// hands down every frame, driving the shared stack's pointer / input. Loaded lazily on its first
    /// activation, unloaded once when the room leaves.
    /// <para>
    /// This is the intended layering, and the reason <see cref="IScreenComponent"/> is an interface rather than
    /// a base class: the page keeps its own <see cref="A"/> / <see cref="Stack"/> fields and its
    /// <see cref="Activated"/> / <see cref="Deactivated"/> tab lifecycle, and gains the engine contract on top
    /// instead of having to reparent onto it. A consumer's own abstract base adds domain lifecycle ABOVE the
    /// interface; the interface never competes for the single base-class slot.
    /// </para>
    /// <para>
    /// Construction and placement are split for the same reason bounds is a per-call parameter: widgets are
    /// built once in <see cref="OnLoad"/>, before any bounds exist, and placed in <see cref="OnLayout"/>, which
    /// re-runs whenever the bounds change. So the page keeps its widget state (typed text, scroll offset, field
    /// values) across a re-layout and needs no resize hook at all.
    /// </para></summary>
    abstract class ToolkitPage : IScreenComponent
    {
        protected GuiAssets A = null!;
        protected ScreenStack Stack = null!;

        Rect _laidOut;
        bool _hasLayout;

        /// <summary>Bind the shared assets + owning stack and build the page. Bounds are NOT passed here: they
        /// arrive per frame, so anything positional belongs in <see cref="OnLayout"/>.</summary>
        public void Load(GuiAssets a, ScreenStack stack)
        {
            A = a; Stack = stack;
            OnLoad();
        }

        /// <summary>Construct widgets and acquire page-owned assets. Runs once, before any bounds are known.</summary>
        protected abstract void OnLoad();

        /// <summary>Place (or re-place) everything positional inside <paramref name="bounds"/>. Runs before the
        /// page's first update or draw, and again only when the bounds actually change.</summary>
        protected virtual void OnLayout(Rect bounds) { }

        public virtual void Activated() { }
        public virtual void Deactivated() { }

        public bool Update(float dt, bool receivesInput, Rect bounds, InputManager input)
        {
            EnsureLayout(bounds);
            return OnUpdate(dt, receivesInput, bounds, input);
        }

        public void Draw(SpriteBatch batch, Rect bounds)
        {
            EnsureLayout(bounds);
            OnDraw(batch, bounds);
        }

        /// <summary>Per-frame update, layout already resolved for <paramref name="bounds"/>. Return whether the
        /// page CONSUMED input, never a bare true (see <see cref="IScreenComponent.Update"/>).</summary>
        protected abstract bool OnUpdate(float dt, bool receivesInput, Rect bounds, InputManager input);

        /// <summary>Per-frame draw, layout already resolved for <paramref name="bounds"/>.</summary>
        protected abstract void OnDraw(SpriteBatch batch, Rect bounds);

        /// <summary>Release page-owned assets. Declared here (rather than left as the <see cref="IScreenComponent"/>
        /// default) so the host can call it through <c>ToolkitPage</c>; pages owning nothing leave it alone.</summary>
        public virtual void UnloadContent() { }

        void EnsureLayout(Rect bounds)
        {
            if (_hasLayout && _laidOut == bounds) return;
            _laidOut = bounds;
            _hasLayout = true;
            OnLayout(bounds);
        }

        // A section header (accent) plus a 1px accent underline across the section width, the shared vertical
        // rhythm the widgets/sprites pages open each column or card with.
        protected void DrawSectionHeader(SpriteBatch batch, StringId title, float x, float y, float width)
        {
            batch.DrawString(A.Small, ((LocalizedText)title).Resolve(), new Vector2(x, y), (Color)GuiTheme.Default.Accent);
            Vector4 accent = GuiTheme.Default.Accent; accent.W = 0.6f;
            batch.Draw(A.White, new Vector4(x, y + A.Small.LineHeight + 2f, width, 1f), (Color)accent);
        }

        protected string Res(StringId id) => ((LocalizedText)id).Resolve();
    }

    /// <summary>Page 1 (the flagship): three aligned columns of retained widgets - form entry, HUD bars, and
    /// skinned nine-slice chrome. No Back button: Esc and the tabs are the navigation.</summary>
    sealed class WidgetsPage : ToolkitPage
    {
        const float ColW = 280f;

        TextInput _name = null!;
        Dropdown _difficulty = null!;
        NumberField _partySize = null!;
        ScrollablePanel _list = null!;
        SlotGrid _slots = null!;
        ProgressBar _progress = null!, _segContinuous = null!, _segDiscrete = null!, _vertBar = null!, _vertPips = null!;
        Button _skinButton = null!, _info = null!, _confirm = null!;
        Panel _skinPanel = null!;
        Label _skinPanelLabel = null!;
        ProgressBar _skinBar = null!;
        Tooltip _tip = null!;

        float _ax, _bx, _cx, _top;

        // Widgets are built here with placeholder bounds and PLACED in OnLayout, so the page survives a bounds
        // change with its typed text, scroll offset and field values intact.
        protected override void OnLoad()
        {
            Rect db = A.Vp.DesignBounds;

            // Column A: form widgets.
            _name = new TextInput(default, A.Small)
            { PlaceholderContent = ShowcaseStrings.WidgetsNamePlaceholder, MaxLength = 16 };
            _difficulty = new Dropdown(
                new[] { new DropdownOption("Easy", 0), new DropdownOption("Normal", 1), new DropdownOption("Hard", 2) },
                default);
            _difficulty.SelectByValue(1);
            _partySize = new NumberField(default, 4f)
            { Min = 1f, Max = 8f, Decimals = 0, DragScale = 0.05f };
            _list = new ScrollablePanel(default) { ItemCount = 24, ItemHeight = 30, ItemSpacing = 4 };

            // Column B: HUD widgets.
            _slots = new SlotGrid(default, count: 10, columns: 5)
            {
                SlotSize = 32f,
                Spacing = 4f,
                KeybindLabels = new[] { "1", "2", "3", "4", "5", "Q", "E", "R", "F", "G" },
                KeybindLabelScale = 0.7f,
            };
            _slots.DrawSlotContent = (slot, rect, b) =>
            {
                if (slot != 0 && slot != 3) return;   // two "icons" as coloured squares, the rest empty
                Color c = slot == 0 ? new Color(0.4f, 0.7f, 1f, 1f) : new Color(0.9f, 0.6f, 0.3f, 1f);
                b.Draw(A.White, new Vector4(rect.X + 8, rect.Y + 8, rect.Width - 16, rect.Height - 16), c);
            };
            _progress = new ProgressBar(default, 0.65f)
            { OverlayText = LocalizedText.Of(ShowcaseStrings.WidgetsLoading, 65) };

            var accent = new Vector4(0.35f, 0.85f, 1f, 1f);
            _segContinuous = new ProgressBar(default, 0.6f)
            { SegmentCount = 6, SegmentSpacing = 3f, FillColor = accent };
            _segDiscrete = new ProgressBar(default, 0.6f)
            { SegmentCount = 5, SegmentSpacing = 4f, SegmentFillMode = SegmentFillMode.Discrete, FillColor = new Vector4(1f, 0.8f, 0.3f, 1f) };
            _vertBar = new ProgressBar(default, 0.5f)
            { FillDirection = FillDirection.BottomToTop, FillColor = new Vector4(0.5f, 1f, 0.6f, 1f) };
            _vertPips = new ProgressBar(default, 0.5f)
            {
                FillDirection = FillDirection.BottomToTop,
                SegmentCount = 4, SegmentSpacing = 4f, SegmentFillMode = SegmentFillMode.Discrete,
                FillColor = new Vector4(1f, 0.5f, 0.5f, 1f),
            };

            // Column C: skinned nine-slice chrome + the primary Confirm at the column bottom.
            GuiStyle skinStyle = A.SkinStyle;
            _skinButton = new Button(default, ShowcaseStrings.WidgetsSkinButton, A.Small) { Style = skinStyle };
            _skinPanel = new Panel(default) { Style = skinStyle, Color = Vector4.One };
            _skinPanelLabel = new Label(default, ShowcaseStrings.WidgetsSkinPanel, A.Small)
            { Align = TextAlign.Center, Color = new Vector4(0.98f, 0.94f, 0.78f, 1f) };
            _skinBar = new ProgressBar(default, 0.7f) { Style = skinStyle, TrackColor = Vector4.One, FillColor = accent };
            _info = new Button(default, ShowcaseStrings.WidgetsHoverForTip, A.Small);
            _confirm = new Button(default, ShowcaseStrings.WidgetsConfirm, A.Small,
                () => Stack.Add(new PopupScreen(A, _name.Text, _difficulty.SelectedLabel))) { Style = GuiStyle.Primary };

            _tip = new Tooltip(A.Small, A.Small) { Viewport = new Vector2(db.Width, db.Height) };
        }

        protected override void OnLayout(Rect bounds)
        {
            _ax = bounds.X; _bx = bounds.X + 320f; _cx = bounds.X + 640f; _top = bounds.Y;

            // Column A: form widgets.
            _name.Bounds = new Rect(_ax, _top + 56f, ColW, 32f);
            _difficulty.TriggerBounds = new Rect(_ax, _top + 122f, 200f, 30f);
            _partySize.Bounds = new Rect(_ax, _top + 188f, 120f, 30f);
            _list.Bounds = new Rect(_ax, _top + 254f, ColW, 170f);

            // Column B: HUD widgets. The slot grid sizes itself from SlotSize/Spacing, so only its origin matters.
            _slots.Bounds = new Rect(_bx, _top + 56f, 0, 0);
            _progress.Bounds = new Rect(_bx, _top + 134f, 260f, 16f);
            _segContinuous.Bounds = new Rect(_bx, _top + 184f, 260f, 12f);
            _segDiscrete.Bounds = new Rect(_bx, _top + 230f, 260f, 12f);
            _vertBar.Bounds = new Rect(_bx, _top + 276f, 14f, 42f);
            _vertPips.Bounds = new Rect(_bx + 22f, _top + 276f, 14f, 42f);

            // Column C: skinned nine-slice chrome + the primary Confirm at the column bottom.
            _skinButton.Bounds = new Rect(_cx, _top + 36f, 160f, 32f);
            _skinPanel.Bounds = new Rect(_cx, _top + 84f, 160f, 40f);
            _skinPanelLabel.Bounds = new Rect(_cx, _top + 84f, 160f, 40f);
            _skinBar.Bounds = new Rect(_cx, _top + 140f, 160f, 34f);
            _info.Bounds = new Rect(_cx, _top + 190f, 160f, 32f);
            // Anchored low as the column's primary action, but kept clear of the shared chrome's translucent
            // controls band (which the point-space hud draws over the bottom ~64 design points).
            _confirm.Bounds = new Rect(_cx, bounds.Bottom - 100f, 160f, 48f);
        }

        protected override bool OnUpdate(float dt, bool receivesInput, Rect bounds, InputManager input)
        {
            // receivesInput is read BEFORE any widget is touched, not just folded into the answer at the end.
            // A widget's Update hit-tests and fires its callbacks, so calling one while a modal above owns input
            // runs the click anyway and only the report comes back false. The IScreenComponent contract is that
            // the COMPONENT still ticks every frame, which is about timers and animation a page owns itself
            // (InputAudioPage keeps its clock running below); it is not licence to keep reading input.
            if (!receivesInput)
            {
                _tip.Hide();   // a hover tooltip must not outlive the frame the page stopped being pointed at
                return false;
            }

            Pointer p = input.Pointer;
            bool consumed = _name.Update(p, input.State, dt);   // true while focused: the field owns the keyboard

            // Dropdown.Update answers "did the SELECTION change", which is not the same question: an open list
            // swallowing a click, or a click that just opened or dismissed it, all report false. Read IsOpen
            // either side of the call instead, so an open dropdown owns input the way a focused TextInput does.
            bool dropdownWasOpen = _difficulty.IsOpen;
            consumed |= _difficulty.Update(p) || dropdownWasOpen || _difficulty.IsOpen;

            // Same shape: NumberField.Update answers "did Value change", so the frames where it owns the keyboard
            // (tap-to-edit) or the pointer (scrub) without landing on a new value would report false.
            consumed |= _partySize.Update(input, dt) || _partySize.IsEditing || _partySize.IsScrubbing;

            _list.Update(p, input.State);
            consumed |= _info.Update(p);
            consumed |= _confirm.Update(p);
            consumed |= _slots.Update(p) >= 0;
            consumed |= _skinButton.Update(p);

            if (p.IsHoveringIn(_info.Bounds))
                _tip.Show(ShowcaseStrings.WidgetsTipTitle,
                    new[]
                    {
                        TooltipLine.Of(ShowcaseStrings.WidgetsTipLine1, new Vector4(0.78f, 0.82f, 0.92f, 1f)),
                        TooltipLine.Of(ShowcaseStrings.WidgetsTipLine2, new Vector4(0.78f, 0.82f, 0.92f, 1f)),
                    },
                    new Vector2(_info.Bounds.X + _info.Bounds.Width * 0.5f, _info.Bounds.Y));
            else _tip.Hide();

            return consumed;
        }

        protected override void OnDraw(SpriteBatch batch, Rect bounds)
        {
            Texture2D white = A.White;
            SpriteFont font = A.Small;
            Vector4 label = GuiTheme.Default.Text;
            Vector4 muted = GuiTheme.Default.TextMuted;

            DrawSectionHeader(batch, ShowcaseStrings.WidgetsSectionForm, _ax, _top, ColW);
            DrawSectionHeader(batch, ShowcaseStrings.WidgetsSectionHud, _bx, _top, ColW);
            DrawSectionHeader(batch, ShowcaseStrings.WidgetsSectionSkin, _cx, _top, ColW);

            // Column A.
            batch.DrawString(font, Res(ShowcaseStrings.WidgetsName), new Vector2(_ax, _top + 36f), (Color)label);
            _name.Draw(batch, white);
            batch.DrawString(font, Res(ShowcaseStrings.WidgetsDifficulty), new Vector2(_ax, _top + 102f), (Color)label);
            batch.DrawString(font, Res(ShowcaseStrings.WidgetsPartySize), new Vector2(_ax, _top + 168f), (Color)label);
            _partySize.Draw(batch, white, font);
            batch.DrawString(font, Res(ShowcaseStrings.WidgetsList), new Vector2(_ax, _top + 234f), (Color)label);

            _list.DrawBackground(batch, white);
            _list.BeginClip(batch);
            for (int i = 0; i < _list.ItemCount; i++)
            {
                Rect r = _list.ItemBounds(i);
                batch.Draw(white, new Vector4(r.X + 4, r.Y, r.Width - 8, r.Height), new Color(0.12f, 0.14f, 0.2f, 1f));
                batch.DrawString(font, $"Item {i + 1}", new Vector2(r.X + 14, r.Y + (r.Height - font.LineHeight) * 0.5f), new Color(0.8f, 0.84f, 0.9f, 1f));
            }
            _list.EndClip(batch);
            _difficulty.Draw(batch, white, font);   // trigger (before its overlay)

            // Column B.
            batch.DrawString(font, Res(ShowcaseStrings.WidgetsHotbar), new Vector2(_bx, _top + 36f), (Color)label);
            _slots.Draw(batch, white, font);
            _progress.Draw(batch, white, font);
            batch.DrawString(font, Res(ShowcaseStrings.WidgetsCastBar), new Vector2(_bx, _top + 166f), (Color)muted);
            _segContinuous.Draw(batch, white);
            batch.DrawString(font, Res(ShowcaseStrings.WidgetsPips), new Vector2(_bx, _top + 212f), (Color)muted);
            _segDiscrete.Draw(batch, white);
            batch.DrawString(font, Res(ShowcaseStrings.WidgetsResource), new Vector2(_bx, _top + 258f), (Color)muted);
            _vertBar.Draw(batch, white);
            _vertPips.Draw(batch, white);

            // Column C.
            _skinButton.Draw(batch, white);
            _skinPanel.Draw(batch, white);
            _skinPanelLabel.Draw(batch);
            _skinBar.Draw(batch, white);
            _info.Draw(batch, white);
            _confirm.Draw(batch, white);

            _difficulty.DrawOverlay(batch, white, font, Stack.Pointer);   // open list on top
            _tip.Draw(batch, white);                                      // tooltip on top of all
        }
    }

    /// <summary>Page 2: two framed cards - textured checker sprites (scale / tint / alpha series) and a runtime TTF
    /// text specimen. Ported from the old 2D room's content onto the shared 920 grid.</summary>
    sealed class SpritesTextPage : ToolkitPage
    {
        Panel _spriteCard = null!, _textCard = null!;
        float _spriteHeaderY, _textHeaderY;

        protected override void OnLoad()
        {
            _spriteCard = new Panel(default) { BorderThickness = 1f };
            _textCard = new Panel(default) { BorderThickness = 1f };
        }

        protected override void OnLayout(Rect bounds)
        {
            float x = bounds.X, w = bounds.Width;
            _spriteHeaderY = bounds.Y;
            _spriteCard.Bounds = new Rect(x, bounds.Y + 28f, w, 176f);
            _textHeaderY = _spriteCard.Bounds.Bottom + 20f;
            _textCard.Bounds = new Rect(x, _textHeaderY + 28f, w, 176f);
        }

        // No interactive widgets, so it never consumes input: a bare `true` here would starve everything below.
        protected override bool OnUpdate(float dt, bool receivesInput, Rect bounds, InputManager input) => false;

        // The four text-specimen lines are runtime TTF type specimens (not player copy), so the raw DrawString
        // literals are the intentional escape hatch. The section headers and sprite captions resolve through
        // StringIds in the same method.
        [LocalizationExempt]
        protected override void OnDraw(SpriteBatch batch, Rect bounds)
        {
            Texture2D white = A.White;

            // Card 1: textured sprites, three bottom-aligned groups (scale / tint / alpha) with a caption each.
            DrawSectionHeader(batch, ShowcaseStrings.SpritesSectionSprites, bounds.X, _spriteHeaderY, bounds.Width);
            _spriteCard.Draw(batch, white);
            Rect card = _spriteCard.Bounds;
            float baseline = card.Y + 118f;
            float g1 = card.X + 40f, g2 = card.X + 350f, g3 = card.X + 640f;

            // Scale series: four checker sprites growing 36 -> 72.
            float sx = g1;
            for (int i = 0; i < 4; i++)
            {
                float s = 36f + i * 12f;
                batch.Draw(A.Checker, new Vector4(sx, baseline - s, s, s), Color.White);
                sx += s + 8f;
            }
            // Tint series: four same-size checker sprites in white / warm / cool / green.
            Vector4[] tints = { Vector4.One, new(1f, 0.7f, 0.4f, 1f), new(0.5f, 0.7f, 1f, 1f), new(0.5f, 1f, 0.6f, 1f) };
            for (int i = 0; i < 4; i++)
                batch.Draw(A.Checker, new Vector4(g2 + i * 56f, baseline - 48f, 48f, 48f), (Color)tints[i]);
            // Alpha series: four same-size checker sprites fading 1.0 -> 0.25.
            float[] alphas = { 1f, 0.7f, 0.45f, 0.25f };
            for (int i = 0; i < 4; i++)
                batch.Draw(A.Checker, new Vector4(g3 + i * 56f, baseline - 48f, 48f, 48f), new Color(1f, 1f, 1f, alphas[i]));

            var caption = GuiTheme.Default.TextMuted;
            float capY = baseline + 10f;
            batch.DrawString(A.Small, Res(ShowcaseStrings.SpritesCaptionScale), new Vector2(g1, capY), (Color)caption);
            batch.DrawString(A.Small, Res(ShowcaseStrings.SpritesCaptionTint), new Vector2(g2, capY), (Color)caption);
            batch.DrawString(A.Small, Res(ShowcaseStrings.SpritesCaptionAlpha), new Vector2(g3, capY), (Color)caption);

            // Card 2: runtime TTF text specimen (raw literals: these are type samples, not copy).
            DrawSectionHeader(batch, ShowcaseStrings.SpritesSectionText, bounds.X, _textHeaderY, bounds.Width);
            _textCard.Draw(batch, white);
            Rect t = _textCard.Bounds;
            batch.DrawString(A.Big, "KhaozEngine.Render2D", new Vector2(t.X + 24f, t.Y + 16f), (Color)GuiTheme.Default.Text);
            batch.DrawString(A.Small, "The quick brown fox jumps over the lazy dog.", new Vector2(t.X + 24f, t.Y + 76f), new Color(0.8f, 0.85f, 0.95f, 1f));
            batch.DrawString(A.Small, "0123456789  !?@#&*()  +-=/<>  {}[]", new Vector2(t.X + 24f, t.Y + 108f), new Color(0.9f, 0.8f, 0.6f, 1f));
            batch.DrawString(A.Small, "Alpha blending, tinting, batched quads on Veldrid.", new Vector2(t.X + 24f, t.Y + 140f), new Color(0.7f, 0.95f, 0.8f, 1f));
        }
    }

    /// <summary>Page 3: gestures (drag / tap / long-press) over a clamped playground, a pause/time-scale orbit, a
    /// clipboard round-trip, and one-shot SFX - the old input-audio room, kept feature-complete. Audio + gestures
    /// live for this page only (created on first activation, disposed when the room leaves).</summary>
    sealed class InputAudioPage : ToolkitPage
    {
        readonly GestureRecognizer _gestures = new();
        readonly GameClock _clock = new();
        readonly List<(Vector2 pos, float life)> _marks = new();

        Panel _panel = null!, _statusCard = null!;
        Rect _playground, _card;
        Vector2 _box, _boxHome, _orbitCenter;
        bool _grabbed;
        bool _boxPlaced;
        float _orbit;

        AudioSystem _audio = null!;
        string _lastSfx = "none";
        string _clipboardStatus = "C copies + verifies a round-trip, V pastes from the OS";
        string _padInfo = "none";

        protected override void OnLoad()
        {
            _panel = new Panel(default) { BorderThickness = 1f };
            _statusCard = new Panel(default) { BorderThickness = 1f };

            // SFX: synth a couple of placeholder sounds into a temp dir, then load + play through the real OpenAL
            // path (same recipe as the old input room). Falls back to a silent backend headless, so this never
            // crashes the room.
            string sfxDir = Path.Combine(Path.GetTempPath(), "ke-showcase-input-sfx");
            Directory.CreateDirectory(sfxDir);
            WavSynth.WriteTone(Path.Combine(sfxDir, "blip.wav"), 880f, 0.12f, Waveform.Sine);
            WavSynth.WriteNoise(Path.Combine(sfxDir, "thud.wav"), 0.20f);
            _audio = new AudioSystem();
            _audio.RegisterSfxes(new[] { "blip", "thud" });
            _audio.LoadContent(sfxDir);
            _audio.SetListener(Vector3.Zero, new Vector3(0, 0, -1), new Vector3(0, 1, 0));
        }

        protected override void OnLayout(Rect bounds)
        {
            _playground = new Rect(bounds.X, bounds.Y, 560f, 400f);
            _panel.Bounds = _playground;
            _card = new Rect(bounds.X + 580f, bounds.Y, 320f, 400f);
            _statusCard.Bounds = _card;
            _boxHome = new Vector2(_playground.X + _playground.Width * 0.28f, _playground.Y + _playground.Height * 0.5f);
            _orbitCenter = new Vector2(_playground.X + _playground.Width * 0.72f, _playground.Y + _playground.Height * 0.30f);
            // Only park the box on the FIRST layout. A later re-layout leaves the player's dragged position
            // alone; Update's clamp pulls it back inside the moved playground on its own.
            if (!_boxPlaced) { _box = _boxHome; _boxPlaced = true; }
        }

        public override void UnloadContent() => _audio.Dispose();

        protected override bool OnUpdate(float dt, bool receivesInput, Rect bounds, InputManager input)
        {
            // The split this page exists to demonstrate. Everything that ACTS on input (plays a sound, writes the
            // clipboard, moves the box) is gated on receivesInput, because those branches fire whether or not the
            // answer is later gated. What keeps running below is the page's own TIMERS, and that is all "a
            // component still updates every frame" ever meant.
            if (receivesInput)
            {
                Pointer pointer = input.Pointer;
                InputState state = input.State;
                _gestures.Update(pointer, dt);   // gestures use REAL dt

                // Clock controls: Space pauses, 1/2/3 set slow/normal/fast.
                if (state.WasPressed(Key.Space)) { if (_clock.IsPaused) _clock.Resume(); else _clock.Pause(); }
                if (state.WasPressed(Key.D1)) _clock.TimeScale = 0.5f;
                if (state.WasPressed(Key.D2)) _clock.TimeScale = 1f;
                if (state.WasPressed(Key.D3)) _clock.TimeScale = 2f;

                // SFX one-shots: Z = non-positional blip, X = positional thud 8 units to the listener's right.
                if (state.WasPressed(Key.Z)) { _audio.PlaySfx("blip"); _lastSfx = "blip"; }
                if (state.WasPressed(Key.X)) { _audio.PlaySfx3D("thud", new Vector3(8, 0, 0)); _lastSfx = "thud (3D)"; }

                // Clipboard: C writes a known string and reads it back (self round-trip). V pastes the OS clipboard.
                if (state.WasPressed(Key.C))
                {
                    string payload = $"KhaozEngine clipboard {_clock.ElapsedScaledSeconds:0.0}s";
                    bool setOk = Clipboard.TrySetClipboardText(payload);
                    string readBack = Clipboard.TryGetClipboardText();
                    bool roundTrip = setOk && readBack == payload;
                    _clipboardStatus = $"copy {(setOk ? "ok" : "FAIL")}, round-trip {(roundTrip ? "PASS" : "FAIL")}: \"{readBack}\"";
                }
                if (state.WasPressed(Key.V))
                {
                    string pasted = Clipboard.TryGetClipboardText();
                    _clipboardStatus = string.IsNullOrEmpty(pasted) ? "paste: <empty / unavailable>" : $"paste: \"{pasted}\"";
                }

                // Gamepad (best-effort): left stick nudges the box, A resets it. No-op with no controller connected.
                var pad = state.PrimaryGamepad;
                if (pad.IsConnected)
                {
                    _box += pad.LeftStickDeadzoned(0.2f) * (260f * dt);
                    if (pad.WasPressed(GamepadButton.A)) _box = _boxHome;
                }
                _padInfo = pad.IsConnected ? $"stick {pad.LeftStick.X:0.0},{pad.LeftStick.Y:0.0}" : "none";

                // Gestures: a drag that starts inside the panel grabs the box, long-press resets it, and taps inside
                // the panel leave fading marks. The box centre is clamped below so nothing leaves the playground.
                var boxRect = new Rect(_box.X - 45, _box.Y - 45, 90, 90);
                if (_gestures.DragStarted && _playground.Contains(_gestures.DragStart) && boxRect.Contains(_gestures.DragStart))
                    _grabbed = true;
                if (_grabbed && _gestures.IsDragging) _box += _gestures.DragDelta;
                if (_gestures.DragEnded) _grabbed = false;
                if (_gestures.Tapped && _playground.Contains(_gestures.TapPosition)) _marks.Add((_gestures.TapPosition, 1f));
                if (_gestures.LongPressed) _box = _boxHome;
            }
            else
            {
                // Drop the drag rather than resume it later: with the gesture recogniser no longer being fed, the
                // DragEnded that would clear this never arrives, and the box would jump on the frame input returns.
                _grabbed = false;
            }

            _audio.Update();
            _clock.Update(dt);
            _orbit += _clock.ScaledDeltaSeconds * 1.6f;   // animation runs on SCALED time (freezes when paused)

            _box.X = Math.Clamp(_box.X, _playground.X + 50f, _playground.Right - 50f);
            _box.Y = Math.Clamp(_box.Y, _playground.Y + 50f, _playground.Bottom - 50f);

            for (int i = _marks.Count - 1; i >= 0; i--)
            {
                var mk = _marks[i];
                mk.life -= dt * 1.2f;
                if (mk.life <= 0f) _marks.RemoveAt(i); else _marks[i] = mk;
            }

            // Consumed only while a drag it started is actually in hand. Hovering, or a drag that began
            // elsewhere, leaves input alone.
            return receivesInput && _grabbed;
        }

        // The "drag me" box label is demo chrome (a raw literal, the intentional escape hatch). The status labels
        // and keys caption in the same method resolve through StringIds, and the diagnostic values are raw by design.
        [LocalizationExempt]
        protected override void OnDraw(SpriteBatch batch, Rect bounds)
        {
            Texture2D white = A.White;
            SpriteFont font = A.Small;

            _panel.Draw(batch, white);

            // Orbiting dot (pause / time-scale made visible) over a backing square, both inside the playground.
            var dot = _orbitCenter + new Vector2(MathF.Cos(_orbit), MathF.Sin(_orbit)) * 90f;
            batch.Draw(white, new Vector4(_orbitCenter.X - 92, _orbitCenter.Y - 92, 184, 184), (Color)GuiTheme.Default.Surface);
            batch.Draw(white, new Vector4(dot.X - 10, dot.Y - 10, 20, 20), new Color(0.95f, 0.75f, 0.35f, 1f));

            foreach (var (pos, life) in _marks)
                batch.Draw(white, new Vector4(pos.X - 6, pos.Y - 6, 12, 12), new Color(0.4f, 0.95f, 0.7f, life));

            var boxColor = _grabbed ? new Vector4(0.30f, 0.55f, 0.75f, 1f) : new Vector4(0.18f, 0.34f, 0.5f, 1f);
            batch.Draw(white, new Vector4(_box.X - 45, _box.Y - 45, 90, 90), (Color)boxColor);
            batch.DrawString(font, "drag me", new Vector2(_box.X - 40, _box.Y - 13), new Color(0.95f, 0.97f, 1f, 1f));

            Pointer pointer = Stack.Pointer;
            float pdx = Math.Clamp(pointer.Position.X, _playground.X, _playground.Right);
            float pdy = Math.Clamp(pointer.Position.Y, _playground.Y, _playground.Bottom);
            batch.Draw(white, new Vector4(pdx - 3, pdy - 3, 6, 6), new Color(0.4f, 0.95f, 0.7f, 1f));

            batch.DrawString(font, Res(ShowcaseStrings.InputKeys), new Vector2(bounds.X, _playground.Bottom + 8f),
                (Color)GuiTheme.Default.TextMuted);

            // Status card: static labels from the catalog, raw diagnostic values (clipboard wraps inside the card).
            _statusCard.Draw(batch, white);
            string gstate = _gestures.IsDragging ? "dragging" : "idle";
            string clock = _clock.IsPaused ? "PAUSED" : $"x{_clock.TimeScale:0.0}";
            DrawRow(batch, ShowcaseStrings.InputGesture, gstate, _card.Y + 20f);
            DrawRow(batch, ShowcaseStrings.InputClock, clock, _card.Y + 68f);
            DrawRow(batch, ShowcaseStrings.InputSimTime, $"{_clock.ElapsedScaledSeconds:0.0}s", _card.Y + 116f);
            DrawRow(batch, ShowcaseStrings.InputLastSfx, _lastSfx, _card.Y + 164f);
            DrawRow(batch, ShowcaseStrings.InputGamepad, _padInfo, _card.Y + 212f);
            batch.DrawString(font, Res(ShowcaseStrings.InputClipboard), new Vector2(_card.X + 16f, _card.Y + 260f), (Color)GuiTheme.Default.TextMuted);
            TextLayout.DrawWrapped(batch, font, _clipboardStatus, new Vector2(_card.X + 16f, _card.Y + 278f),
                _card.Width - 32f, TextAlign.Left, (Color)GuiTheme.Default.Text);
        }

        void DrawRow(SpriteBatch batch, StringId label, string value, float y)
        {
            batch.DrawString(A.Small, Res(label), new Vector2(_card.X + 16f, y), (Color)GuiTheme.Default.TextMuted);
            batch.DrawString(A.Small, value, new Vector2(_card.X + 16f, y + 18f), (Color)GuiTheme.Default.Text);
        }
    }

    /// <summary>Page 4: the immediate-mode GuiSurface demo - every widget issued inside Draw with no retained
    /// instances. Aligned to the shared 920 grid, with dynamic/diagnostic labels so they stay raw.</summary>
    // A low-level GuiSurface demonstration whose labels are alignment names, toggle state, and a PointerCaptured
    // readout, so it uses LocalizedText.Raw throughout and is marked exempt from the analyzer.
    [LocalizationExempt]
    sealed class ImmediatePage : ToolkitPage
    {
        GuiSurface _ui = null!;
        bool _toggled;

        protected override void OnLoad() => _ui = new GuiSurface(A.White);

        // Immediate mode: nothing is retained, so there is nothing to lay out and nothing to consume here. The
        // widgets are issued (and hit-tested) inside OnDraw.
        protected override bool OnUpdate(float dt, bool receivesInput, Rect bounds, InputManager input) => false;

        protected override void OnDraw(SpriteBatch batch, Rect bounds)
        {
            float x = bounds.X;
            // Draw carries no input parameter by design, so an immediate-mode surface reads the pointer from the
            // page's own host reference. That is exactly why the fan-out contract stays free of resource and
            // input arguments: a component that needs more takes it in its constructor or from its own host.
            _ui.Begin(batch, Stack.Pointer);

            // Titled intro card.
            var card = new Rect(x, bounds.Y, bounds.Width, 92f);
            _ui.Panel(card, new Vector4(0.11f, 0.14f, 0.20f, 1f), new Vector4(0.30f, 0.38f, 0.52f, 1f));
            _ui.Label(A.Big, LocalizedText.Raw("Immediate-mode GuiSurface"), new Vector2(card.X + 18, card.Y + 14), Vector4.One);
            _ui.Label(A.Small, LocalizedText.Raw("One call per widget inside Draw - no retained instances."),
                new Vector2(card.X + 18, card.Y + 60), new Vector4(0.6f, 0.7f, 0.85f, 1f));

            // Three same-width cells showing Left / Center / Right alignment.
            var labelColor = new Vector4(0.82f, 0.86f, 0.94f, 1f);
            var cellFill = new Vector4(0.10f, 0.12f, 0.17f, 1f);
            for (int i = 0; i < 3; i++)
            {
                var cell = new Rect(x + i * 300f, bounds.Y + 118f, 280f, 36f);
                _ui.Panel(cell, cellFill);
                var align = (GuiAlign)i;
                _ui.Label(A.Small, cell, LocalizedText.Raw(align.ToString()), labelColor, align);
            }

            // A row of 4 colour swatches.
            _ui.Label(A.Small, LocalizedText.Raw("Swatches"), new Vector2(x, bounds.Y + 174f), labelColor);
            Vector4[] cols =
            {
                new(0.85f, 0.30f, 0.32f, 1f),
                new(0.32f, 0.74f, 0.42f, 1f),
                new(0.34f, 0.55f, 0.90f, 1f),
                new(0.92f, 0.78f, 0.30f, 1f),
            };
            for (int i = 0; i < cols.Length; i++)
                _ui.Swatch(new Rect(x + i * 56f, bounds.Y + 200f, 48f, 48f), cols[i]);

            // Semantic button presets (crisp theme): Primary/Secondary/Danger/Active + one disabled.
            float by = bounds.Y + 272f;
            if (_ui.Button(A.Small, new Rect(x, by, 150f, 44f), LocalizedText.Raw(_toggled ? "PRIMARY ON" : "PRIMARY"), GuiStyle.Primary))
                _toggled = !_toggled;
            _ui.Button(A.Small, new Rect(x + 165f, by, 150f, 44f), LocalizedText.Raw("Secondary"), GuiStyle.Secondary);
            _ui.Button(A.Small, new Rect(x + 330f, by, 150f, 44f), LocalizedText.Raw("Danger"), GuiStyle.Danger);
            _ui.Button(A.Small, new Rect(x + 495f, by, 150f, 44f), LocalizedText.Raw("Active"), GuiStyle.Active, enabled: true, selected: true);
            _ui.Button(A.Small, new Rect(x + 660f, by, 150f, 44f), LocalizedText.Raw("Disabled"), GuiStyle.Secondary, enabled: false);

            // Capture-flag readout.
            _ui.Label(A.Small, LocalizedText.Raw($"PointerCaptured: {_ui.PointerCaptured}"),
                new Vector2(x, bounds.Y + 344f), new Vector4(0.6f, 0.7f, 0.85f, 1f));
        }
    }

    /// <summary>Page 5: launchers for the screen-stack demos - a modal Settings dialog, a transparent pause
    /// overlay, and the turn-key patch-notes panel - each a button plus a muted caption. Pushing these onto the
    /// stack over the tab host IS the screen-stack story (modals freeze it, the overlay shows it through a scrim).</summary>
    sealed class ScreensPage : ToolkitPage
    {
        Button _settings = null!, _overlay = null!, _patchNotes = null!, _toasts = null!;

        protected override void OnLoad()
        {
            _settings = new Button(default, ShowcaseStrings.ScreensSettings, A.Small,
                () => Stack.Add(new SettingsScreen(A)));
            _overlay = new Button(default, ShowcaseStrings.ScreensOverlay, A.Small,
                () => Stack.Add(new OverlayScreen(A)));
            _patchNotes = new Button(default, ShowcaseStrings.ScreensPatchNotes, A.Small,
                () => Stack.Add(new PatchNotesScreen(PatchNotesLoader.Load(typeof(Room2DGui).Assembly), A.Small, A.White, A.Vp)));
            _toasts = new Button(default, ShowcaseStrings.ScreensToasts, A.Small,
                () => Stack.Add(new ToastsScreen(A, A.Toasts)));
        }

        protected override void OnLayout(Rect bounds)
        {
            float x = bounds.X, w = 260f;
            _settings.Bounds = new Rect(x, bounds.Y + 74f, w, 44f);
            _overlay.Bounds = new Rect(x, bounds.Y + 138f, w, 44f);
            _patchNotes.Bounds = new Rect(x, bounds.Y + 202f, w, 44f);
            _toasts.Bounds = new Rect(x, bounds.Y + 266f, w, 44f);
        }

        protected override bool OnUpdate(float dt, bool receivesInput, Rect bounds, InputManager input)
        {
            // Nothing here animates, so the whole body sits behind the gate. Button.Update hit-tests AND fires its
            // OnClick, and every one of these pushes a screen: gating only the returned bool would push it anyway
            // while telling the host nothing was consumed.
            if (!receivesInput) return false;

            // Button.Update returns whether it was clicked, so the page's consumed answer is simply "did one of
            // my buttons take this frame's tap". Never a bare true just because buttons exist.
            Pointer p = input.Pointer;
            bool consumed = _settings.Update(p);
            consumed |= _overlay.Update(p);
            consumed |= _patchNotes.Update(p);
            consumed |= _toasts.Update(p);
            return consumed;
        }

        protected override void OnDraw(SpriteBatch batch, Rect bounds)
        {
            Texture2D white = A.White;
            SpriteFont font = A.Small;
            var muted = GuiTheme.Default.TextMuted;
            float capX = bounds.X + 280f;

            batch.DrawString(font, Res(ShowcaseStrings.ScreensIntro), new Vector2(bounds.X, bounds.Y + 24f), (Color)GuiTheme.Default.Text);

            _settings.Draw(batch, white);
            batch.DrawString(font, Res(ShowcaseStrings.ScreensSettingsCaption), new Vector2(capX, _settings.Bounds.Y + 12f), (Color)muted);
            _overlay.Draw(batch, white);
            batch.DrawString(font, Res(ShowcaseStrings.ScreensOverlayCaption), new Vector2(capX, _overlay.Bounds.Y + 12f), (Color)muted);
            _patchNotes.Draw(batch, white);
            batch.DrawString(font, Res(ShowcaseStrings.ScreensPatchNotesCaption), new Vector2(capX, _patchNotes.Bounds.Y + 12f), (Color)muted);
            _toasts.Draw(batch, white);
            batch.DrawString(font, Res(ShowcaseStrings.ScreensToastsCaption), new Vector2(capX, _toasts.Bounds.Y + 12f), (Color)muted);
        }
    }

    // The volume readout is a dynamic "%" value (a number), legitimately non-localizable, so it uses
    // LocalizedText.Raw. Marking the screen [LocalizationExempt] tells the analyzer that Raw here is intentional.
    [LocalizationExempt]
    sealed class SettingsScreen : Screen
    {
        readonly GuiAssets _a;
        Panel _dialog = null!;
        Label _title = null!, _volumeLabel = null!, _fullscreenLabel = null!, _help = null!, _readout = null!;
        Slider _volume = null!;
        Toggle _fullscreen = null!;
        Button _back = null!;

        // Keyboard/gamepad: FocusNavigator picks the focused row (0 = volume, 1 = fullscreen), then the focused
        // widget reads input through the stack's shared InputManager. Up/Down moves focus, Left/Right adjusts,
        // Enter flips the toggle, Esc backs out (handled by Room2DGui). Pointer still works on every row.
        readonly FocusNavigator _nav = new(count: 2);
        Rect _volumeRow, _fullscreenRow;

        public SettingsScreen(GuiAssets a)
        {
            _a = a;
            DrawOrder = 10;
            PassUpdateThrough = false;     // modal: the tab host beneath neither updates nor receives input
            TransitionOnDuration = 0.18f;
            TransitionOffDuration = 0.18f;
        }

        public override void LoadContent()
        {
            // Center the dialog in the design space. Inner widgets are placed relative to the dialog rect.
            Rect d = Layout.Resolve(_a.Vp.DesignBounds, Anchor.Center, 440, 330);
            _dialog = new Panel(d) { BorderThickness = 1f };
            _title = new Label(Layout.Resolve(d, Anchor.Top, d.Width, 44, marginY: 20), ShowcaseStrings.SettingsTitle, _a.Big) { Align = TextAlign.Center };

            _volumeLabel = new Label(new Rect(d.X + 30, d.Y + 86, 120, 24), ShowcaseStrings.SettingsVolume, _a.Small);
            _volume = new Slider(new Rect(d.X + 160, d.Y + 90, 200, 14), 0.7f);
            _readout = new Label(new Rect(d.X + 370, d.Y + 86, 50, 24), LocalizedText.Raw("70%"), _a.Small) { Align = TextAlign.Right };

            _fullscreenLabel = new Label(new Rect(d.X + 30, d.Y + 136, 200, 26), ShowcaseStrings.SettingsFullscreen, _a.Small);
            _fullscreen = new Toggle(new Rect(d.Right - 76, d.Y + 134, 56, 28));

            // Row bands the focus ring highlights (span the dialog inside its margin).
            _volumeRow = new Rect(d.X + 16, d.Y + 80, d.Width - 32, 36);
            _fullscreenRow = new Rect(d.X + 16, d.Y + 128, d.Width - 32, 40);

            _help = new Label(new Rect(d.X + 30, d.Y + 178, d.Width - 60, 70),
                ShowcaseStrings.SettingsHelp, _a.Small)
            { Wrap = true, Color = GuiTheme.Default.TextMuted };

            Rect backRect = Layout.Resolve(d, Anchor.Bottom, 180, 50, marginY: 18);
            _back = new Button(backRect, ShowcaseStrings.CommonBack, _a.Small, ExitScreen);
        }

        public override bool Update(float dt, bool receivesInput)
        {
            if (receivesInput)
            {
                var im = Manager.InputManager;
                _nav.Update(im);                                    // Up/Down (or D-pad / stick) moves focus
                _volume.Update(im, focused: _nav.Focused == 0);     // focused row also takes Left/Right
                _fullscreen.Update(im, focused: _nav.Focused == 1); // and Enter to flip
                _back.Update(Manager.Pointer);                      // Back stays pointer, Esc backs out (Room2DGui)
                _readout.Content = LocalizedText.Raw($"{(int)(_volume.Value * 100)}%");
            }
            return true;
        }

        public override void Draw(SpriteBatch batch)
        {
            Rect db = _a.Vp.DesignBounds;
            batch.Draw(_a.White, new Vector4(db.X, db.Y, db.Width, db.Height), new Color(0, 0, 0, 0.55f * TransitionAlpha));   // scrim
            _dialog.Draw(batch, _a.White);
            _title.Draw(batch);

            // Focus ring around the keyboard/gamepad-focused row (accent hairline, matching the crisp look).
            Rect ring = _nav.Focused == 1 ? _fullscreenRow : _volumeRow;
            DrawBorder(batch, _a.White, ring, 1.5f, new Vector4(0.35f, 0.62f, 1f, 0.9f * TransitionAlpha));

            _volumeLabel.Draw(batch);
            _volume.Draw(batch, _a.White);
            _readout.Draw(batch);
            _fullscreenLabel.Draw(batch);
            _fullscreen.Draw(batch, _a.White);
            _help.Draw(batch);
            _back.Draw(batch, _a.White);
        }

        // Hairline rectangle outline (4 edges) via the shared white texture - the focus ring.
        static void DrawBorder(SpriteBatch batch, Texture2D white, Rect r, float t, Vector4 c)
        {
            var col = (Color)c;
            batch.Draw(white, new Vector4(r.X, r.Y, r.Width, t), col);              // top
            batch.Draw(white, new Vector4(r.X, r.Bottom - t, r.Width, t), col);     // bottom
            batch.Draw(white, new Vector4(r.X, r.Y, t, r.Height), col);             // left
            batch.Draw(white, new Vector4(r.Right - t, r.Y, t, r.Height), col);     // right
        }
    }

    /// <summary>A modal popup pushed as its own screen, driven by PopupPanel, summarising the widgets page's
    /// entered name + difficulty.</summary>
    sealed class PopupScreen : Screen
    {
        readonly GuiAssets _a;
        readonly string _name, _difficulty;
        PopupPanel _popup = null!;

        public PopupScreen(GuiAssets a, string name, string difficulty)
        {
            _a = a; _name = name; _difficulty = difficulty;
            DrawOrder = 20; PassUpdateThrough = false;
            TransitionOnDuration = 0.15f; TransitionOffDuration = 0.15f;
        }

        // The popup's chrome (title, buttons, headers, static note) is real copy resolved through StringId. Only
        // the user-entered name/difficulty VALUES are raw (a typed name is not a localizable key), so the method is
        // exempt from KELOC002 for those LocalizedText.Raw calls.
        [LocalizationExempt]
        public override void LoadContent()
        {
            _popup = new PopupPanel
            {
                Viewport = new Vector2(_a.Vp.Width, _a.Vp.Height),
                TitleContent = ShowcaseStrings.PopupTitle,
                DismissContent = ShowcaseStrings.PopupCancel,
                PrimaryActionContent = ShowcaseStrings.PopupStart,
                ShowPrimaryAction = true,
                TitleFont = _a.Small,
                BodyFont = _a.Small,
            };
            var valueColor = new Vector4(0.7f, 0.85f, 1f, 1f);
            _popup.SetRows(new[]
            {
                PopupRow.Header(ShowcaseStrings.PopupSummary),
                PopupRow.Stat(ShowcaseStrings.PopupName,
                    string.IsNullOrEmpty(_name) ? ShowcaseStrings.PopupUnnamed : LocalizedText.Raw(_name), valueColor),
                PopupRow.Stat(ShowcaseStrings.PopupDifficulty, LocalizedText.Raw(_difficulty), valueColor),
                PopupRow.Spacer(),
                PopupRow.Stat(ShowcaseStrings.PopupNote, ShowcaseStrings.PopupNoteBody, new Vector4(0.6f, 0.65f, 0.75f, 1f)),
            });
        }

        public override bool Update(float dt, bool receivesInput)
        {
            if (receivesInput)
            {
                bool dismissed = _popup.Update(Manager.Pointer);
                if (dismissed || _popup.WasPrimaryActionClicked) ExitScreen();
            }
            return true;
        }

        public override void Draw(SpriteBatch batch) => _popup.Draw(batch, _a.White, Manager.Pointer);
    }

    /// <summary>Ported from <c>SceneSample</c>'s PauseScene, translated from the <c>GameScene</c>
    /// DrawBelow/UpdateBelow overlay pattern into a <see cref="ScreenStack"/> screen: no
    /// <see cref="Screen.BackgroundColor"/> (so the tab host still shows through the semi-transparent scrim drawn
    /// here) and <see cref="Screen.PassUpdateThrough"/> left false (modal: the host below freezes while this overlay
    /// is up, matching SceneSample's UpdateBelow=false). Pushed directly over the tab host, which is the overlay's
    /// host now.</summary>
    sealed class OverlayScreen : Screen
    {
        readonly GuiAssets _a;
        Label _label = null!;
        Button _resume = null!;

        public OverlayScreen(GuiAssets a)
        {
            _a = a;
            DrawOrder = 10;
            PassUpdateThrough = false;   // modal: freezes the host below (SceneSample's UpdateBelow=false)
            TransitionOnDuration = 0.15f;
            TransitionOffDuration = 0.15f;
        }

        public override void LoadContent()
        {
            Rect db = _a.Vp.DesignBounds;
            _label = new Label(Layout.Resolve(db, Anchor.Center, db.Width, 32), ShowcaseStrings.OverlayPaused, _a.Big) { Align = TextAlign.Center };
            _resume = new Button(Layout.Resolve(db, Anchor.Center, 200, 52, marginY: -80), ShowcaseStrings.OverlayResume, _a.Small, ExitScreen);
        }

        public override bool Update(float dt, bool receivesInput)
        {
            // Esc is handled centrally by Room2DGui.OnUpdate (exits whichever screen is topmost), so this screen
            // only needs to drive its own Resume button.
            if (receivesInput) _resume.Update(Manager.Pointer);
            return true;
        }

        public override void Draw(SpriteBatch batch)
        {
            // No DrawBackground: the tab host underneath still shows (this screen has no BackgroundColor), exactly
            // like SceneSample's PauseScene (DrawBelow=true) letting the frozen play scene show through.
            Rect db = _a.Vp.DesignBounds;
            batch.Draw(_a.White, new Vector4(db.X, db.Y, db.Width, db.Height), new Color(0f, 0f, 0f, 0.5f * TransitionAlpha));   // scrim
            _label.Draw(batch);
            _resume.Draw(batch, _a.White);
        }
    }

    /// <summary>Permanent, passthrough overlay screen hosting the <see cref="ToastView"/> on top of the whole
    /// stack. DrawOrder 100 keeps it above every demo screen (Settings 10, Popup 20), so the stack updates it
    /// FIRST and draws it LAST each frame. <see cref="Screen.AlwaysReceivesInput"/> keeps toasts tappable even
    /// while a modal below has consumed input, and <see cref="Screen.PassUpdateThrough"/> (with the
    /// dismissal-only consumed return from <see cref="Update"/>) means it never freezes or input-starves the
    /// screens below. Tap-dismiss runs against the stack's own <see cref="ScreenStack.Pointer"/>, the exact
    /// instance every sibling screen hit-tests, and a dismissing tap calls <c>Pointer.ConsumeGesture</c> inside
    /// <see cref="ToastView.Update"/>, so the same gesture can't also fire a button underneath the toast. The
    /// view's <c>Pointer.BlockRegion</c> calls additionally protect a beneath layer that checks
    /// <c>Pointer.IsBlocked</c> explicitly (a game world under the UI). The <see cref="ToastStack"/> model is NOT
    /// ticked here: <see cref="Room2DGui.OnUpdate"/> ticks it with the raw frame dt, because a real game's
    /// ScreenStack dt may be sim-scaled while toasts must count down at real speed.</summary>
    sealed class ToastOverlayScreen : Screen
    {
        readonly GuiAssets _a;
        readonly ToastView _view;

        public ToastOverlayScreen(GuiAssets a, ToastStack toasts)
        {
            _a = a;
            _view = new ToastView(toasts, a.Small) { Bounds = a.Vp.DesignBounds };
            DrawOrder = 100;              // topmost: updated first, drawn last
            AlwaysReceivesInput = true;   // toasts stay tappable under a modal
            PassUpdateThrough = true;     // never blocks the screens below (transitions stay at the zero default)
        }

        public override bool Update(float dt, bool receivesInput)
        {
            // Returns true only on an actual tap-dismiss, per the Screen.Update contract for permanently
            // mounted overlays (the dismissal-only return keeps the reported consumption honest and keeps this
            // pattern portable to screens that don't set AlwaysReceivesInput).
            if (!receivesInput) return false;
            return _view.Update(Manager.Pointer);
        }

        public override void Draw(SpriteBatch batch) => _view.Draw(batch, _a.White, _a.Small);
    }

    /// <summary>Demo screen for the toast stack, pushed from the Screens page: a button per <see cref="ToastKind"/>,
    /// a sticky toast that only dismisses on tap, a keyed toast that replaces itself in place (with an incrementing
    /// counter baked into the message) on every click, and clearing that key. The <see cref="ToastStack"/> itself
    /// belongs to <see cref="Room2DGui"/> and its view to the permanent <see cref="ToastOverlayScreen"/>, not to
    /// this screen, so toasts fired here keep counting down and stay visible even after Back returns to the tab
    /// host.</summary>
    sealed class ToastsScreen : Screen
    {
        /// <summary>The replacement key shared by the Update-keyed and Clear-keyed buttons below.</summary>
        const string DemoKey = "demo";

        readonly GuiAssets _a;
        readonly ToastStack _toasts;
        Label _title = null!;
        Button _standard = null!, _warning = null!, _danger = null!, _sticky = null!, _update = null!, _clear = null!, _back = null!;
        int _counter;

        public ToastsScreen(GuiAssets a, ToastStack toasts)
        {
            _a = a; _toasts = toasts;
            PassUpdateThrough = false;
            BackgroundColor = GuiTheme.Default.Background;   // opaque full screen
        }

        public override void LoadContent()
        {
            Rect db = _a.Vp.DesignBounds;
            _title = new Label(Layout.Resolve(db, Anchor.Top, db.Width, 40, marginY: 28), ShowcaseStrings.ToastsTitle, _a.Big) { Align = TextAlign.Center };

            // Centered vertical button column, same convention as the old GUI menu screen.
            Rect mid = Layout.Resolve(db, Anchor.Center, 240, 52);
            _standard = new Button(mid with { Y = mid.Y - 192 }, ShowcaseStrings.ToastsStandard, _a.Small,
                () => _toasts.Show(ShowcaseStrings.ToastsStandardMessage, ToastKind.Standard));
            _warning = new Button(mid with { Y = mid.Y - 128 }, ShowcaseStrings.ToastsWarning, _a.Small,
                () => _toasts.Show(ShowcaseStrings.ToastsWarningMessage, ToastKind.Warning));
            _danger = new Button(mid with { Y = mid.Y - 64 }, ShowcaseStrings.ToastsDanger, _a.Small,
                () => _toasts.Show(ShowcaseStrings.ToastsDangerMessage, ToastKind.Danger));
            _sticky = new Button(mid with { Y = mid.Y }, ShowcaseStrings.ToastsSticky, _a.Small,
                () => _toasts.ShowSticky(ShowcaseStrings.ToastsStickyMessage));
            _update = new Button(mid with { Y = mid.Y + 64 }, ShowcaseStrings.ToastsUpdate, _a.Small,
                () => _toasts.Show(LocalizedText.Of(ShowcaseStrings.ToastsCounterMessage, ++_counter), key: DemoKey));
            _clear = new Button(mid with { Y = mid.Y + 128 }, ShowcaseStrings.ToastsClear, _a.Small,
                () => _toasts.Clear(DemoKey));
            _back = new Button(mid with { Y = mid.Y + 192 }, ShowcaseStrings.CommonBack, _a.Small, ExitScreen);
        }

        public override bool Update(float dt, bool receivesInput)
        {
            if (!receivesInput) return false;
            var p = Manager.Pointer;
            _standard.Update(p);
            _warning.Update(p);
            _danger.Update(p);
            _sticky.Update(p);
            _update.Update(p);
            _clear.Update(p);
            _back.Update(p);
            return true;
        }

        public override void Draw(SpriteBatch batch)
        {
            DrawBackground(batch, _a.White, _a.Vp);
            _title.Draw(batch);
            _standard.Draw(batch, _a.White);
            _warning.Draw(batch, _a.White);
            _danger.Draw(batch, _a.White);
            _sticky.Draw(batch, _a.White);
            _update.Draw(batch, _a.White);
            _clear.Draw(batch, _a.White);
            _back.Draw(batch, _a.White);
        }
    }
}
