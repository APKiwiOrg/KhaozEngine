using System;
using System.Numerics;
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
        /// construction and before the room is pushed.</summary>
        public RoomGui Init(Texture2D white, SpriteFont big, SpriteFont small)
        {
            _assets = new GuiAssets(white, big, small);
            return this;
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

    /// <summary>Shared white texture + two font sizes, mirroring <c>GuiSample</c>'s <c>GuiAssets</c>.</summary>
    sealed class GuiAssets
    {
        public readonly Texture2D White;
        public readonly SpriteFont Big, Small;
        public GuiAssets(Texture2D white, SpriteFont big, SpriteFont small) { White = white; Big = big; Small = small; }
    }

    /// <summary>Root screen: buttons push the modal Settings / heavy Widgets / immediate-mode screens, or the
    /// overlay demo. No Quit button (leaving the room is Esc, handled by <see cref="RoomGui"/> itself).</summary>
    sealed class GuiMenuScreen : Screen
    {
        readonly GuiAssets _a;
        readonly IDesignViewport _vp;
        Label _title = null!, _footer = null!;
        Button _settings = null!, _widgets = null!, _immediate = null!, _overlay = null!;

        public GuiMenuScreen(GuiAssets a, IDesignViewport vp)
        {
            _a = a; _vp = vp;
            PassUpdateThrough = false;
            BackgroundColor = new Vector4(0.07f, 0.09f, 0.13f, 1f);   // opaque full screen
        }

        public override void LoadContent()
        {
            Rect db = _vp.DesignBounds;
            _title = new Label(Layout.Resolve(db, Anchor.Top, db.Width, 56, marginY: 56), "GUI + Widgets", _a.Big) { Align = TextAlign.Center };

            // Centered vertical button column, anchored to the design center so it stays put at any window size.
            Rect mid = Layout.Resolve(db, Anchor.Center, 220, 52);
            _settings = new Button(mid with { Y = mid.Y - 96 }, "Settings", _a.Small, () => Manager.Add(new SettingsScreen(_a, _vp)));
            _widgets = new Button(mid with { Y = mid.Y - 32 }, "Widgets", _a.Small, () => Manager.Add(new WidgetsScreen(_a, _vp)));
            _immediate = new Button(mid with { Y = mid.Y + 32 }, "Immediate", _a.Small, () => Manager.Add(new ImmediateScreen(_a, _vp)));
            _overlay = new Button(mid with { Y = mid.Y + 96 }, "Overlay demo", _a.Small, () => Manager.Add(new OverlayHostScreen(_a, _vp)));

            _footer = new Label(Layout.Resolve(db, Anchor.Bottom, db.Width, 24, marginY: 36),
                "Settings = core widgets    Widgets = heavy widgets    Immediate = GuiSurface    Overlay demo = push/pop    Esc for menu", _a.Small)
            { Align = TextAlign.Center, Color = new Vector4(0.6f, 0.7f, 0.85f, 1f) };
        }

        public override bool Update(float dt, bool receivesInput)
        {
            if (!receivesInput) return false;
            _settings.Update(Manager.Pointer);
            _widgets.Update(Manager.Pointer);
            _immediate.Update(Manager.Pointer);
            _overlay.Update(Manager.Pointer);
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
            _footer.Draw(batch);
        }
    }

    sealed class SettingsScreen : Screen
    {
        readonly GuiAssets _a;
        readonly IDesignViewport _vp;
        Panel _dialog = null!;
        Label _title = null!, _volumeLabel = null!, _fullscreenLabel = null!, _help = null!, _readout = null!;
        Slider _volume = null!;
        Toggle _fullscreen = null!;
        Button _back = null!;

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
            _title = new Label(Layout.Resolve(d, Anchor.Top, d.Width, 44, marginY: 20), "Settings", _a.Big) { Align = TextAlign.Center };

            _volumeLabel = new Label(new Rect(d.X + 30, d.Y + 86, 120, 24), "Volume", _a.Small);
            _volume = new Slider(new Rect(d.X + 160, d.Y + 90, 200, 14), 0.7f);
            _readout = new Label(new Rect(d.X + 370, d.Y + 86, 50, 24), "70%", _a.Small) { Align = TextAlign.Right };

            _fullscreenLabel = new Label(new Rect(d.X + 30, d.Y + 136, 200, 26), "Fullscreen", _a.Small);
            _fullscreen = new Toggle(new Rect(d.Right - 76, d.Y + 134, 56, 28));

            _help = new Label(new Rect(d.X + 30, d.Y + 178, d.Width - 60, 70),
                "Drag the slider or toggle the switch. This help text wraps to the panel width via TextLayout.",
                _a.Small)
            { Wrap = true, Color = new Vector4(0.6f, 0.7f, 0.85f, 1f) };

            Rect backRect = Layout.Resolve(d, Anchor.Bottom, 180, 50, marginY: 18);
            _back = new Button(backRect, "Back", _a.Small, ExitScreen);
        }

        public override bool Update(float dt, bool receivesInput)
        {
            if (receivesInput)
            {
                _volume.Update(Manager.Pointer);
                _fullscreen.Update(Manager.Pointer);
                _back.Update(Manager.Pointer);
                _readout.Text = $"{(int)(_volume.Value * 100)}%";
            }
            return true;
        }

        public override void Draw(SpriteBatch batch)
        {
            Rect db = _vp.DesignBounds;
            batch.Draw(_a.White, new Vector4(db.X, db.Y, db.Width, db.Height), new Color(0, 0, 0, 0.55f * TransitionAlpha));   // scrim
            _dialog.Draw(batch, _a.White);
            _title.Draw(batch);
            _volumeLabel.Draw(batch);
            _volume.Draw(batch, _a.White);
            _readout.Draw(batch);
            _fullscreenLabel.Draw(batch);
            _fullscreen.Draw(batch, _a.White);
            _help.Draw(batch);
            _back.Draw(batch, _a.White);
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

        public WidgetsScreen(GuiAssets a, IDesignViewport vp)
        {
            _a = a; _vp = vp;
            PassUpdateThrough = false;
            BackgroundColor = new Vector4(0.07f, 0.09f, 0.13f, 1f);   // opaque full screen (no bleed-through)
        }

        public override void LoadContent()
        {
            Rect db = _vp.DesignBounds;
            _title = new Label(Layout.Resolve(db, Anchor.Top, db.Width, 40, marginY: 28), "Widgets", _a.Big) { Align = TextAlign.Center };

            _nameLabel = new Label(new Rect(120, 92, 260, 18), "Name", _a.Small);
            _name = new TextInput(new Rect(120, 112, 260, 32), _a.Small) { Placeholder = "type a name", MaxLength = 16 };

            _diffLabel = new Label(new Rect(120, 158, 260, 18), "Difficulty", _a.Small);
            _difficulty = new Dropdown(
                new[] { new DropdownOption("Easy", 0), new DropdownOption("Normal", 1), new DropdownOption("Hard", 2) },
                new Rect(120, 178, 180, 30));
            _difficulty.SelectByValue(1);

            _listLabel = new Label(new Rect(120, 222, 260, 18), "Scrollable list (wheel / drag)", _a.Small);
            _list = new ScrollablePanel(new Rect(120, 244, 280, 200)) { ItemCount = 24, ItemHeight = 30, ItemSpacing = 4 };

            _tip = new Tooltip(_a.Small, _a.Small) { Viewport = new Vector2(db.Width, db.Height) };
            _info = new Button(new Rect(620, 112, 160, 32), "hover for tip", _a.Small);
            _confirm = new Button(new Rect(620, 380, 160, 48), "Confirm...", _a.Small, () => Manager.Add(new PopupScreen(_a, _vp, _name.Text, _difficulty.SelectedLabel)));
            _back = new Button(new Rect(620, 440, 160, 40), "Back", _a.Small, ExitScreen);
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

            if (p.IsHoveringIn(_info.Bounds))
                _tip.Show("Tooltip",
                    new[]
                    {
                        new TooltipLine("Auto-sized, flips when", new Vector4(0.78f, 0.82f, 0.92f, 1f)),
                        new TooltipLine("it would clip the top.", new Vector4(0.78f, 0.82f, 0.92f, 1f)),
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

        public override void LoadContent()
        {
            _popup = new PopupPanel
            {
                Viewport = new Vector2(_vp.Width, _vp.Height),
                Title = "Start game?",
                DismissText = "Cancel",
                PrimaryActionText = "Start",
                ShowPrimaryAction = true,
                TitleFont = _a.Small,
                BodyFont = _a.Small,
            };
            _popup.SetRows(new[]
            {
                PopupRow.Header("Summary"),
                PopupRow.Stat("Name", string.IsNullOrEmpty(_name) ? "(unnamed)" : _name, new Vector4(0.7f, 0.85f, 1f, 1f)),
                PopupRow.Stat("Difficulty", _difficulty, new Vector4(0.7f, 0.85f, 1f, 1f)),
                PopupRow.Spacer(),
                PopupRow.Stat("Note", "Cancel or Start below.", new Vector4(0.6f, 0.65f, 0.75f, 1f)),
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
            BackgroundColor = new Vector4(0.07f, 0.09f, 0.13f, 1f);   // opaque full screen
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
            _ui.Label(_a.Big, "Immediate-mode GuiSurface", new Vector2(card.X + 18, card.Y + 14), Vector4.One);
            _ui.Label(_a.Small, "One call per widget inside Draw - no retained instances.",
                new Vector2(card.X + 18, card.Y + 64), new Vector4(0.6f, 0.7f, 0.85f, 1f));

            // Three same-width rects showing Left / Center / Right alignment.
            var labelColor = new Vector4(0.82f, 0.86f, 0.94f, 1f);
            var cellFill = new Vector4(0.10f, 0.12f, 0.17f, 1f);
            for (int i = 0; i < 3; i++)
            {
                var cell = new Rect(120 + i * 250, 170, 230, 36);
                _ui.Panel(cell, cellFill);
                var align = (GuiAlign)i;
                _ui.Label(_a.Small, cell, align.ToString(), labelColor, align);
            }

            // A row of 4 swatches in different colours.
            _ui.Label(_a.Small, "Swatches", new Vector2(120, 222), labelColor);
            Vector4[] cols =
            {
                new(0.85f, 0.30f, 0.32f, 1f),
                new(0.32f, 0.74f, 0.42f, 1f),
                new(0.34f, 0.55f, 0.90f, 1f),
                new(0.92f, 0.78f, 0.30f, 1f),
            };
            for (int i = 0; i < cols.Length; i++)
                _ui.Swatch(new Rect(120 + i * 56, 248, 48, 48), cols[i]);

            // Buttons: enabled toggle, disabled, selected.
            if (_ui.Button(_a.Small, new Rect(120, 320, 200, 48), _toggled ? "ON" : "OFF"))
                _toggled = !_toggled;
            _ui.Button(_a.Small, new Rect(340, 320, 200, 48), "Disabled", GuiStyle.Default, enabled: false);
            _ui.Button(_a.Small, new Rect(560, 320, 200, 48), "Selected", GuiStyle.Default, enabled: true, selected: true);

            // Capture-flag readout + Back.
            _ui.Label(_a.Small, $"PointerCaptured: {_ui.PointerCaptured}",
                new Vector2(120, 400), new Vector4(0.6f, 0.7f, 0.85f, 1f));
            if (_ui.Button(_a.Small, new Rect(120, 440, 200, 48), "Back"))
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
            _title = new Label(Layout.Resolve(db, Anchor.Top, db.Width, 40, marginY: 28), "Overlay demo", _a.Big) { Align = TextAlign.Center };
            _hint = new Label(Layout.Resolve(db, Anchor.Center, db.Width, 24), "Push overlay puts a transparent, dismissable pause screen on top of this one.", _a.Small)
            { Align = TextAlign.Center, Color = new Vector4(0.7f, 0.9f, 0.75f, 1f) };
            _pause = new Button(Layout.Resolve(db, Anchor.Center, 200, 52, marginY: -80), "Push overlay", _a.Small, () => Manager.Add(new OverlayScreen(_a, _vp)));
            _back = new Button(Layout.Resolve(db, Anchor.Bottom, 180, 50, marginY: 24), "Back", _a.Small, ExitScreen);
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
            _label = new Label(Layout.Resolve(db, Anchor.Center, db.Width, 32), "PAUSED - Esc to resume", _a.Big) { Align = TextAlign.Center };
            _resume = new Button(Layout.Resolve(db, Anchor.Center, 200, 52, marginY: -80), "Resume", _a.Small, ExitScreen);
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
