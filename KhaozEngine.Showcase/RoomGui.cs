using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>Ported from <c>GuiSample/Program.cs</c> (a <see cref="ScreenStack"/> of widget screens: menu,
    /// modal Settings, heavy Widgets, immediate-mode) and <c>SceneSample/Program.cs</c> (the push/pop transparent
    /// overlay demo, folded in here as the "Overlay demo" screen: it pushes a screen that stays visible but
    /// freezes/dims the one below it, same as SceneSample's PauseScene did with DrawBelow/UpdateBelow on
    /// GameScene). The room owns the <see cref="ScreenStack"/> and drives it every frame from
    /// <see cref="OnUpdate"/>/<see cref="OnDraw2D"/>, using the room's own <see cref="GameScene.Manager"/> context
    /// (Input/Pointer/Viewport) exactly as <c>ShowcaseApp</c> feeds <c>SceneManager</c>. A <see cref="GameScene"/>
    /// cannot reach <c>Surface2D</c> itself, so <see cref="ShowcaseApp"/> creates the texture/fonts and hands them
    /// in via <see cref="Init"/> right after construction, keeping the constructor parameterless for the room
    /// registry's <c>Func&lt;GameScene&gt;</c>.</summary>
    public sealed class RoomGui : GameScene
    {
        GuiAssets _assets = null!;
        ScreenStack _stack = null!;

        /// <summary>Wire in the textures/fonts created on the app's Surface2D. Call once, right after
        /// construction and before the room is pushed. <paramref name="skin"/> is the nine-slice frame skin used by
        /// the "skinned chrome" widgets on the Widgets screen (baked via <see cref="BakeFramePixels"/>).</summary>
        public RoomGui Init(Texture2D white, SpriteFont big, SpriteFont small, GuiSkin skin)
        {
            _assets = new GuiAssets(white, big, small, skin);
            return this;
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
            _stack = new ScreenStack();
            _stack.Add(new GuiMenuScreen(_assets, Manager!.Viewport!));
        }

        public override void OnUpdate(float dt)
        {
            var m = Manager!;
            // Esc backs out exactly one level. With a modal sub-screen open (Settings/Widgets/Immediate/Overlay),
            // Esc exits the topmost screen (which plays its off-transition) and returns to the root menu. Only
            // once the stack is back down to just the root menu screen does Esc leave the room via Manager.Pop().
            // Centralized here so no sub-screen needs its own Esc handler (avoids a double-pop on the same frame).
            if (m.Input.WasPressed(Key.Escape))
            {
                if (_stack.Screens.Count <= 1) { m.Pop(); return; }
                _stack.Screens[^1].ExitScreen();
                return;
            }
            _stack.Update(dt, m.Input, m.Viewport);
        }

        public override void OnDraw2D(SpriteBatch batch) => _stack.Draw(batch);
    }

    /// <summary>Shared white texture + two font sizes + the demo nine-slice skin, mirroring <c>GuiSample</c>'s
    /// <c>GuiAssets</c>.</summary>
    sealed class GuiAssets
    {
        public readonly Texture2D White;
        public readonly SpriteFont Big, Small;
        public readonly GuiSkin Skin;
        public GuiAssets(Texture2D white, SpriteFont big, SpriteFont small, GuiSkin skin)
        {
            White = white; Big = big; Small = small; Skin = skin;
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

    /// <summary>Root screen: buttons push the modal Settings / heavy Widgets / immediate-mode screens, the
    /// overlay demo, or the Patch Notes panel. No Quit button (leaving the room is Esc, handled by
    /// <see cref="RoomGui"/> itself).</summary>
    sealed class GuiMenuScreen : Screen
    {
        readonly GuiAssets _a;
        readonly IDesignViewport _vp;
        Label _title = null!, _footer = null!;
        Button _settings = null!, _widgets = null!, _immediate = null!, _overlay = null!, _patchNotes = null!;

        public GuiMenuScreen(GuiAssets a, IDesignViewport vp)
        {
            _a = a; _vp = vp;
            PassUpdateThrough = false;
            BackgroundColor = GuiTheme.Default.Background;   // opaque full screen
        }

        public override void LoadContent()
        {
            Rect db = _vp.DesignBounds;
            _title = new Label(Layout.Resolve(db, Anchor.Top, db.Width, 56, marginY: 56), ShowcaseStrings.GuiTitle, _a.Big) { Align = TextAlign.Center };

            // Centered vertical button column, anchored to the design center so it stays put at any window size.
            Rect mid = Layout.Resolve(db, Anchor.Center, 220, 52);
            _settings = new Button(mid with { Y = mid.Y - 128 }, ShowcaseStrings.MenuSettings, _a.Small, () => Manager.Add(new SettingsScreen(_a, _vp)));
            _widgets = new Button(mid with { Y = mid.Y - 64 }, ShowcaseStrings.MenuWidgets, _a.Small, () => Manager.Add(new WidgetsScreen(_a, _vp)));
            _immediate = new Button(mid with { Y = mid.Y }, ShowcaseStrings.MenuImmediate, _a.Small, () => Manager.Add(new ImmediateScreen(_a, _vp)));
            _overlay = new Button(mid with { Y = mid.Y + 64 }, ShowcaseStrings.MenuOverlayDemo, _a.Small, () => Manager.Add(new OverlayHostScreen(_a, _vp)));
            _patchNotes = new Button(mid with { Y = mid.Y + 128 }, ShowcaseStrings.MenuPatchNotes, _a.Small,
                () => Manager.Add(new PatchNotesScreen(PatchNotesLoader.Load(typeof(RoomGui).Assembly), _a.Small, _a.White, _vp)));

            _footer = new Label(Layout.Resolve(db, Anchor.Bottom, db.Width, 24, marginY: 36),
                ShowcaseStrings.MenuFooter, _a.Small)
            { Align = TextAlign.Center, Color = GuiTheme.Default.TextMuted };
        }

        public override bool Update(float dt, bool receivesInput)
        {
            if (!receivesInput) return false;
            _settings.Update(Manager.Pointer);
            _widgets.Update(Manager.Pointer);
            _immediate.Update(Manager.Pointer);
            _overlay.Update(Manager.Pointer);
            _patchNotes.Update(Manager.Pointer);
            return true;
        }

        public override void Draw(SpriteBatch batch)
        {
            DrawBackground(batch, _a.White, _vp);
            _title.Draw(batch);
            _settings.Draw(batch, _a.White);
            _widgets.Draw(batch, _a.White);
            _immediate.Draw(batch, _a.White);
            _overlay.Draw(batch, _a.White);
            _patchNotes.Draw(batch, _a.White);
            _footer.Draw(batch);
        }
    }

    // The volume readout is a dynamic "%" value (a number), legitimately non-localizable, so it uses
    // LocalizedText.Raw. Marking the screen [LocalizationExempt] tells the analyzer that Raw here is intentional.
    [LocalizationExempt]
    sealed class SettingsScreen : Screen
    {
        readonly GuiAssets _a;
        readonly IDesignViewport _vp;
        Panel _dialog = null!;
        Label _title = null!, _volumeLabel = null!, _fullscreenLabel = null!, _help = null!, _readout = null!;
        Slider _volume = null!;
        Toggle _fullscreen = null!;
        Button _back = null!;

        // Keyboard/gamepad: FocusNavigator picks the focused row (0 = volume, 1 = fullscreen), then the focused
        // widget reads input through the stack's shared InputManager. Up/Down moves focus, Left/Right adjusts,
        // Enter flips the toggle, Esc backs out (handled by RoomGui). Pointer still works on every row.
        readonly FocusNavigator _nav = new(count: 2);
        Rect _volumeRow, _fullscreenRow;

        public SettingsScreen(GuiAssets a, IDesignViewport vp)
        {
            _a = a; _vp = vp;
            DrawOrder = 10;
            PassUpdateThrough = false;     // modal: the menu beneath neither updates nor receives input
            TransitionOnDuration = 0.18f;
            TransitionOffDuration = 0.18f;
        }

        public override void LoadContent()
        {
            // Center the dialog in the design space. Inner widgets are placed relative to the dialog rect.
            Rect d = Layout.Resolve(_vp.DesignBounds, Anchor.Center, 440, 330);
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
                ShowcaseStrings.SettingsHelp,
                _a.Small)
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
                _back.Update(Manager.Pointer);                      // Back stays pointer; Esc backs out (RoomGui)
                _readout.Content = LocalizedText.Raw($"{(int)(_volume.Value * 100)}%");
            }
            return true;
        }

        public override void Draw(SpriteBatch batch)
        {
            Rect db = _vp.DesignBounds;
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

    /// <summary>Heavy widgets: text entry, dropdown, scrollable list (scissor-clipped), a hover tooltip, and a
    /// modal popup pushed as its own screen.</summary>
    sealed class WidgetsScreen : Screen
    {
        readonly GuiAssets _a;
        readonly IDesignViewport _vp;
        Label _title = null!, _nameLabel = null!, _diffLabel = null!, _listLabel = null!;
        TextInput _name = null!;
        Dropdown _difficulty = null!;
        ScrollablePanel _list = null!;
        Tooltip _tip = null!;
        Button _info = null!, _confirm = null!, _back = null!;
        Label _slotsLabel = null!;
        SlotGrid _slots = null!;
        ProgressBar _progress = null!;

        // Item 1-3 coverage: a skinned button/panel/bar (beside the flat widgets above), a segmented cast/pip bar,
        // and a vertical bar.
        Label _skinLabel = null!;
        Button _skinButton = null!;
        Panel _skinPanel = null!;
        Label _skinPanelLabel = null!;
        ProgressBar _skinBar = null!, _segContinuous = null!, _segDiscrete = null!, _vertBar = null!, _vertPips = null!;

        public WidgetsScreen(GuiAssets a, IDesignViewport vp)
        {
            _a = a; _vp = vp;
            PassUpdateThrough = false;
            BackgroundColor = GuiTheme.Default.Background;   // opaque full screen (no bleed-through)
        }

        public override void LoadContent()
        {
            Rect db = _vp.DesignBounds;
            _title = new Label(Layout.Resolve(db, Anchor.Top, db.Width, 40, marginY: 28), ShowcaseStrings.WidgetsTitle, _a.Big) { Align = TextAlign.Center };

            _nameLabel = new Label(new Rect(120, 92, 260, 18), ShowcaseStrings.WidgetsName, _a.Small);
            _name = new TextInput(new Rect(120, 112, 260, 32), _a.Small) { PlaceholderContent = ShowcaseStrings.WidgetsNamePlaceholder, MaxLength = 16 };

            _diffLabel = new Label(new Rect(120, 158, 260, 18), ShowcaseStrings.WidgetsDifficulty, _a.Small);
            _difficulty = new Dropdown(
                new[] { new DropdownOption("Easy", 0), new DropdownOption("Normal", 1), new DropdownOption("Hard", 2) },
                new Rect(120, 178, 180, 30));
            _difficulty.SelectByValue(1);

            _listLabel = new Label(new Rect(120, 222, 260, 18), ShowcaseStrings.WidgetsList, _a.Small);
            _list = new ScrollablePanel(new Rect(120, 244, 280, 200)) { ItemCount = 24, ItemHeight = 30, ItemSpacing = 4 };

            _tip = new Tooltip(_a.Small, _a.Small) { Viewport = new Vector2(db.Width, db.Height) };
            _info = new Button(new Rect(620, 112, 160, 32), ShowcaseStrings.WidgetsHoverForTip, _a.Small);
            _confirm = new Button(new Rect(620, 380, 160, 48), ShowcaseStrings.WidgetsConfirm, _a.Small, () => Manager.Add(new PopupScreen(_a, _vp, _name.Text, _difficulty.SelectedLabel)));
            _back = new Button(new Rect(620, 440, 160, 40), ShowcaseStrings.CommonBack, _a.Small, ExitScreen);

            _slotsLabel = new Label(new Rect(440, 92, 220, 18), ShowcaseStrings.WidgetsHotbar, _a.Small);
            _slots = new SlotGrid(new Rect(440, 112, 0, 0), count: 10, columns: 5)
            {
                SlotSize = 32f,
                Spacing = 4f,
                KeybindLabels = new[] { "1", "2", "3", "4", "5", "Q", "E", "R", "F", "G" },
                KeybindLabelScale = 0.7f,
            };
            // Content hook: the widget knows nothing about items, so the caller paints two "icons" as coloured squares.
            _slots.DrawSlotContent = (slot, rect, b) =>
            {
                if (slot != 0 && slot != 3) return;
                Color c = slot == 0 ? new Color(0.4f, 0.7f, 1f, 1f) : new Color(0.9f, 0.6f, 0.3f, 1f);
                b.Draw(_a.White, new Vector4(rect.X + 8, rect.Y + 8, rect.Width - 16, rect.Height - 16), c);
            };

            _progress = new ProgressBar(new Rect(440, 200, 176, 16), 0.65f)
            {
                OverlayText = LocalizedText.Of(ShowcaseStrings.WidgetsLoading, 65),
            };

            // Skinned chrome (nine-slice) beside the flat widgets, plus a segmented + a vertical bar.
            GuiStyle skinStyle = _a.SkinStyle;
            var accent = new Vector4(0.35f, 0.85f, 1f, 1f);

            _skinLabel = new Label(new Rect(620, 148, 170, 18), ShowcaseStrings.WidgetsSkinTitle, _a.Small);
            _skinButton = new Button(new Rect(620, 168, 160, 32), ShowcaseStrings.WidgetsSkinButton, _a.Small) { Style = skinStyle };
            _skinPanel = new Panel(new Rect(620, 206, 160, 40)) { Style = skinStyle, Color = Vector4.One };
            _skinPanelLabel = new Label(new Rect(620, 206, 160, 40), ShowcaseStrings.WidgetsSkinPanel, _a.Small)
            { Align = TextAlign.Center, Color = new Vector4(0.98f, 0.94f, 0.78f, 1f) };
            _skinBar = new ProgressBar(new Rect(620, 254, 160, 18), 0.7f) { Style = skinStyle, TrackColor = Vector4.One, FillColor = accent };

            // Segmented cast bar (continuous, tick separators) and a discrete pip bar, both at 0.6.
            _segContinuous = new ProgressBar(new Rect(620, 282, 160, 12), 0.6f)
            { SegmentCount = 6, SegmentSpacing = 3f, FillColor = accent };
            _segDiscrete = new ProgressBar(new Rect(620, 300, 160, 12), 0.6f)
            { SegmentCount = 5, SegmentSpacing = 4f, SegmentFillMode = SegmentFillMode.Discrete, FillColor = new Vector4(1f, 0.8f, 0.3f, 1f) };

            // Vertical resource bars (BottomToTop): a continuous one and a discrete pip stack.
            _vertBar = new ProgressBar(new Rect(620, 324, 14, 52), 0.5f)
            { FillDirection = FillDirection.BottomToTop, FillColor = new Vector4(0.5f, 1f, 0.6f, 1f) };
            _vertPips = new ProgressBar(new Rect(642, 324, 14, 52), 0.5f)
            {
                FillDirection = FillDirection.BottomToTop,
                SegmentCount = 4, SegmentSpacing = 4f, SegmentFillMode = SegmentFillMode.Discrete,
                FillColor = new Vector4(1f, 0.5f, 0.5f, 1f),
            };
        }

        public override bool Update(float dt, bool receivesInput)
        {
            if (!receivesInput) return false;
            var p = Manager.Pointer;
            _name.Update(p, Manager.Input, dt);
            _difficulty.Update(p);
            _list.Update(p, Manager.Input);
            _info.Update(p);
            _confirm.Update(p);
            _back.Update(p);
            _slots.Update(p);
            _skinButton.Update(p);

            if (p.IsHoveringIn(_info.Bounds))
                _tip.Show(ShowcaseStrings.WidgetsTipTitle,
                    new[]
                    {
                        TooltipLine.Of(ShowcaseStrings.WidgetsTipLine1, new Vector4(0.78f, 0.82f, 0.92f, 1f)),
                        TooltipLine.Of(ShowcaseStrings.WidgetsTipLine2, new Vector4(0.78f, 0.82f, 0.92f, 1f)),
                    },
                    new Vector2(_info.Bounds.X + _info.Bounds.Width * 0.5f, _info.Bounds.Y));
            else _tip.Hide();
            return true;
        }

        public override void Draw(SpriteBatch batch)
        {
            DrawBackground(batch, _a.White, _vp);
            _title.Draw(batch);
            _nameLabel.Draw(batch);
            _name.Draw(batch, _a.White);
            _diffLabel.Draw(batch);
            _listLabel.Draw(batch);

            // Scrollable list: background, then clipped rows.
            _list.DrawBackground(batch, _a.White);
            _list.BeginClip(batch);
            for (int i = 0; i < _list.ItemCount; i++)
            {
                Rect r = _list.ItemBounds(i);
                batch.Draw(_a.White, new Vector4(r.X + 4, r.Y, r.Width - 8, r.Height), new Color(0.12f, 0.14f, 0.2f, 1f));
                batch.DrawString(_a.Small, $"Item {i + 1}", new Vector2(r.X + 14, r.Y + (r.Height - _a.Small.LineHeight) * 0.5f), new Color(0.8f, 0.84f, 0.9f, 1f));
            }
            _list.EndClip(batch);

            _difficulty.Draw(batch, _a.White, _a.Small);   // dropdown trigger (before overlay)
            _info.Draw(batch, _a.White);
            _confirm.Draw(batch, _a.White);
            _back.Draw(batch, _a.White);

            _slotsLabel.Draw(batch);
            _slots.Draw(batch, _a.White, _a.Small);
            _progress.Draw(batch, _a.White, _a.Small);

            // Skinned chrome + segmented / vertical bars.
            _skinLabel.Draw(batch);
            _skinButton.Draw(batch, _a.White);
            _skinPanel.Draw(batch, _a.White);
            _skinPanelLabel.Draw(batch);
            _skinBar.Draw(batch, _a.White);
            _segContinuous.Draw(batch, _a.White);
            _segDiscrete.Draw(batch, _a.White);
            _vertBar.Draw(batch, _a.White);
            _vertPips.Draw(batch, _a.White);

            _difficulty.DrawOverlay(batch, _a.White, _a.Small, Manager.Pointer);   // open list on top
            _tip.Draw(batch, _a.White);                                            // tooltip on top of all
        }
    }

    /// <summary>A modal popup pushed as its own screen, driven by PopupPanel.</summary>
    sealed class PopupScreen : Screen
    {
        readonly GuiAssets _a;
        readonly IDesignViewport _vp;
        readonly string _name, _difficulty;
        PopupPanel _popup = null!;

        public PopupScreen(GuiAssets a, IDesignViewport vp, string name, string difficulty)
        {
            _a = a; _vp = vp; _name = name; _difficulty = difficulty;
            DrawOrder = 20; PassUpdateThrough = false;
            TransitionOnDuration = 0.15f; TransitionOffDuration = 0.15f;
        }

        // The popup's chrome (title, buttons, headers, static note) is real copy resolved through StringId; only
        // the user-entered name/difficulty VALUES are raw (a typed name is not a localizable key), so the method is
        // exempt from KELOC002 for those LocalizedText.Raw calls.
        [LocalizationExempt]
        public override void LoadContent()
        {
            _popup = new PopupPanel
            {
                Viewport = new Vector2(_vp.Width, _vp.Height),
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

    /// <summary>Immediate-mode demo: every widget is issued inside Draw via GuiSurface (no retained widget fields).
    /// Hit-testing and rendering both happen in the GuiSurface calls, so Update is a no-op input gate.</summary>
    // A low-level GuiSurface demonstration whose labels are dynamic/diagnostic (alignment names, toggle state,
    // a PointerCaptured readout), so it uses LocalizedText.Raw throughout and is marked exempt from the analyzer.
    [LocalizationExempt]
    sealed class ImmediateScreen : Screen
    {
        readonly GuiAssets _a;
        readonly IDesignViewport _vp;
        GuiSurface _ui = null!;
        bool _toggled;

        public ImmediateScreen(GuiAssets a, IDesignViewport vp)
        {
            _a = a; _vp = vp;
            PassUpdateThrough = false;
            BackgroundColor = GuiTheme.Default.Background;   // opaque full screen
        }

        public override void LoadContent() => _ui = new GuiSurface(_a.White);

        public override bool Update(float dt, bool receivesInput) => receivesInput;

        public override void Draw(SpriteBatch batch)
        {
            DrawBackground(batch, _a.White, _vp);
            _ui.Begin(batch, Manager.Pointer);

            // Titled card near the top.
            var card = new Rect(120, 40, 720, 110);
            _ui.Panel(card, new Vector4(0.11f, 0.14f, 0.20f, 1f), new Vector4(0.30f, 0.38f, 0.52f, 1f));
            _ui.Label(_a.Big, LocalizedText.Raw("Immediate-mode GuiSurface"), new Vector2(card.X + 18, card.Y + 14), Vector4.One);
            _ui.Label(_a.Small, LocalizedText.Raw("One call per widget inside Draw - no retained instances."),
                new Vector2(card.X + 18, card.Y + 64), new Vector4(0.6f, 0.7f, 0.85f, 1f));

            // Three same-width rects showing Left / Center / Right alignment.
            var labelColor = new Vector4(0.82f, 0.86f, 0.94f, 1f);
            var cellFill = new Vector4(0.10f, 0.12f, 0.17f, 1f);
            for (int i = 0; i < 3; i++)
            {
                var cell = new Rect(120 + i * 250, 170, 230, 36);
                _ui.Panel(cell, cellFill);
                var align = (GuiAlign)i;
                _ui.Label(_a.Small, cell, LocalizedText.Raw(align.ToString()), labelColor, align);
            }

            // A row of 4 swatches in different colours.
            _ui.Label(_a.Small, LocalizedText.Raw("Swatches"), new Vector2(120, 222), labelColor);
            Vector4[] cols =
            {
                new(0.85f, 0.30f, 0.32f, 1f),
                new(0.32f, 0.74f, 0.42f, 1f),
                new(0.34f, 0.55f, 0.90f, 1f),
                new(0.92f, 0.78f, 0.30f, 1f),
            };
            for (int i = 0; i < cols.Length; i++)
                _ui.Swatch(new Rect(120 + i * 56, 248, 48, 48), cols[i]);

            // Semantic button presets (10.11.0 crisp theme): Primary/Secondary/Danger/Active + a disabled one.
            if (_ui.Button(_a.Small, new Rect(120, 320, 150, 44), LocalizedText.Raw(_toggled ? "PRIMARY ON" : "PRIMARY"), GuiStyle.Primary))
                _toggled = !_toggled;
            _ui.Button(_a.Small, new Rect(285, 320, 150, 44), LocalizedText.Raw("Secondary"), GuiStyle.Secondary);
            _ui.Button(_a.Small, new Rect(450, 320, 150, 44), LocalizedText.Raw("Danger"), GuiStyle.Danger);
            _ui.Button(_a.Small, new Rect(615, 320, 150, 44), LocalizedText.Raw("Active"), GuiStyle.Active, enabled: true, selected: true);

            // Capture-flag readout + Back.
            _ui.Label(_a.Small, LocalizedText.Raw($"PointerCaptured: {_ui.PointerCaptured}"),
                new Vector2(120, 400), new Vector4(0.6f, 0.7f, 0.85f, 1f));
            if (_ui.Button(_a.Small, new Rect(120, 440, 200, 48), LocalizedText.Raw("Back")))
                Manager.Remove(this);
        }
    }

    /// <summary>Ported from <c>SceneSample</c>'s PlayScene: a full screen with a marker. The button below pushes
    /// the transparent pause overlay on top of it (Esc here backs out to the room's root menu, same as any other
    /// sub-screen, per <see cref="RoomGui"/>'s centralized Esc handling).</summary>
    sealed class OverlayHostScreen : Screen
    {
        readonly GuiAssets _a;
        readonly IDesignViewport _vp;
        Label _title = null!, _hint = null!;
        Button _pause = null!, _back = null!;

        public OverlayHostScreen(GuiAssets a, IDesignViewport vp)
        {
            _a = a; _vp = vp;
            PassUpdateThrough = false;
            BackgroundColor = new Vector4(0.08f, 0.20f, 0.12f, 1f);   // opaque full screen (SceneSample's PlayScene green)
        }

        public override void LoadContent()
        {
            Rect db = _vp.DesignBounds;
            _title = new Label(Layout.Resolve(db, Anchor.Top, db.Width, 40, marginY: 28), ShowcaseStrings.OverlayTitle, _a.Big) { Align = TextAlign.Center };
            _hint = new Label(Layout.Resolve(db, Anchor.Center, db.Width, 24), ShowcaseStrings.OverlayHint, _a.Small)
            { Align = TextAlign.Center, Color = new Vector4(0.7f, 0.9f, 0.75f, 1f) };
            _pause = new Button(Layout.Resolve(db, Anchor.Center, 200, 52, marginY: -80), ShowcaseStrings.OverlayPush, _a.Small, () => Manager.Add(new OverlayScreen(_a, _vp)));
            _back = new Button(Layout.Resolve(db, Anchor.Bottom, 180, 50, marginY: 24), ShowcaseStrings.CommonBack, _a.Small, ExitScreen);
        }

        public override bool Update(float dt, bool receivesInput)
        {
            if (!receivesInput) return false;
            _pause.Update(Manager.Pointer);
            _back.Update(Manager.Pointer);
            return true;
        }

        public override void Draw(SpriteBatch batch)
        {
            DrawBackground(batch, _a.White, _vp);
            _title.Draw(batch);
            _hint.Draw(batch);
            _pause.Draw(batch, _a.White);
            _back.Draw(batch, _a.White);
        }
    }

    /// <summary>Ported from <c>SceneSample</c>'s PauseScene, translated from the <c>GameScene</c>
    /// DrawBelow/UpdateBelow overlay pattern into a <see cref="ScreenStack"/> screen: no
    /// <see cref="Screen.BackgroundColor"/> (so the screen below still shows through the semi-transparent scrim
    /// drawn here) and <see cref="Screen.PassUpdateThrough"/> left false (modal: the host screen below freezes
    /// while this overlay is up, matching SceneSample's UpdateBelow=false).</summary>
    sealed class OverlayScreen : Screen
    {
        readonly GuiAssets _a;
        readonly IDesignViewport _vp;
        Label _label = null!;
        Button _resume = null!;

        public OverlayScreen(GuiAssets a, IDesignViewport vp)
        {
            _a = a; _vp = vp;
            DrawOrder = 10;
            PassUpdateThrough = false;   // modal: freezes the host screen below (SceneSample's UpdateBelow=false)
            TransitionOnDuration = 0.15f;
            TransitionOffDuration = 0.15f;
        }

        public override void LoadContent()
        {
            Rect db = _vp.DesignBounds;
            _label = new Label(Layout.Resolve(db, Anchor.Center, db.Width, 32), ShowcaseStrings.OverlayPaused, _a.Big) { Align = TextAlign.Center };
            _resume = new Button(Layout.Resolve(db, Anchor.Center, 200, 52, marginY: -80), ShowcaseStrings.OverlayResume, _a.Small, ExitScreen);
        }

        public override bool Update(float dt, bool receivesInput)
        {
            // Esc is handled centrally by RoomGui.OnUpdate (exits whichever screen is topmost), so this screen
            // only needs to drive its own Resume button.
            if (receivesInput) _resume.Update(Manager.Pointer);
            return true;
        }

        public override void Draw(SpriteBatch batch)
        {
            // No DrawBackground: the host screen underneath still shows (this screen has no BackgroundColor),
            // exactly like SceneSample's PauseScene (DrawBelow=true) letting the frozen play scene show through.
            Rect db = _vp.DesignBounds;
            batch.Draw(_a.White, new Vector4(db.X, db.Y, db.Width, db.Height), new Color(0f, 0f, 0f, 0.5f * TransitionAlpha));   // scrim
            _label.Draw(batch);
            _resume.Draw(batch, _a.White);
        }
    }
}
