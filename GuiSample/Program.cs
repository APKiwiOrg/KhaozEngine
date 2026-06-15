using System;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

// Proves the screen-stack on the custom stack: a menu screen whose buttons push a modal settings screen,
// all routed through ScreenStack (input-consumption layering + transitions) over AppWindow + Render2D.
var window = new AppWindow("KhaozEngine.Gui — screen stack", 960, 540) { ClearColor = new Vector4(0.07f, 0.09f, 0.13f, 1f) };
var surface = new Render2DSurface(window);
var assets = new GuiAssets(
    surface.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1),
    surface.LoadFont("/System/Library/Fonts/Supplemental/Arial.ttf", 40f),
    surface.LoadFont("/System/Library/Fonts/Supplemental/Arial.ttf", 26f));

var stack = new ScreenStack();
stack.Add(new MenuScreen(assets, () => window.Close()));

window.Run(frame =>
{
    if (frame.Input.WasPressed(Key.Escape)) window.Close();
    stack.Update(frame.Dt, frame.Input);

    surface.NewFrame(frame);
    surface.Batch.Begin();
    stack.Draw(surface.Batch);
    surface.Batch.End();
});

surface.Dispose();
window.Dispose();

sealed class GuiAssets
{
    public readonly Texture2D White;
    public readonly SpriteFont Big, Small;
    public GuiAssets(Texture2D white, SpriteFont big, SpriteFont small) { White = white; Big = big; Small = small; }
}

sealed class MenuScreen : Screen
{
    readonly GuiAssets _a;
    readonly Action _quit;
    Button _settings = null!, _widgets = null!, _quitBtn = null!;

    public MenuScreen(GuiAssets a, Action quit) { _a = a; _quit = quit; PassUpdateThrough = false; }

    public override void LoadContent()
    {
        _settings = new Button(new Rect(380, 200, 200, 52), "Settings", _a.Small, () => Manager.Add(new SettingsScreen(_a)));
        _widgets = new Button(new Rect(380, 264, 200, 52), "Widgets", _a.Small, () => Manager.Add(new WidgetsScreen(_a)));
        _quitBtn = new Button(new Rect(380, 328, 200, 52), "Quit", _a.Small, _quit);
    }

    public override bool Update(float dt, bool receivesInput)
    {
        if (!receivesInput) return false;
        _settings.Update(Manager.Pointer);
        _widgets.Update(Manager.Pointer);
        _quitBtn.Update(Manager.Pointer);
        return true;
    }

    public override void Draw(SpriteBatch batch)
    {
        batch.DrawString(_a.Big, "Main Menu", new Vector2(380, 120), new Vector4(0.95f, 0.97f, 1f, 1f));
        _settings.Draw(batch, _a.White);
        _widgets.Draw(batch, _a.White);
        _quitBtn.Draw(batch, _a.White);
        batch.DrawString(_a.Small, "Settings = core widgets  •  Widgets = dropdown/text/scroll/popup  •  Esc to quit", new Vector2(140, 470), new Vector4(0.6f, 0.7f, 0.85f, 1f));
    }
}

sealed class SettingsScreen : Screen
{
    readonly GuiAssets _a;
    Panel _dialog = null!;
    Label _title = null!, _volumeLabel = null!, _fullscreenLabel = null!, _help = null!, _readout = null!;
    Slider _volume = null!;
    Toggle _fullscreen = null!;
    Button _back = null!;

    public SettingsScreen(GuiAssets a)
    {
        _a = a;
        DrawOrder = 10;
        PassUpdateThrough = false;     // modal: the menu beneath neither updates nor receives input
        TransitionOnDuration = 0.18f;
        TransitionOffDuration = 0.18f;
    }

    public override void LoadContent()
    {
        _dialog = new Panel(new Rect(260, 110, 440, 330)) { BorderThickness = 1f };
        _title = new Label(new Rect(260, 130, 440, 44), "Settings", _a.Big) { Align = TextAlign.Center };

        _volumeLabel = new Label(new Rect(290, 196, 120, 24), "Volume", _a.Small);
        _volume = new Slider(new Rect(420, 200, 200, 14), 0.7f);
        _readout = new Label(new Rect(630, 196, 50, 24), "70%", _a.Small) { Align = TextAlign.Right };

        _fullscreenLabel = new Label(new Rect(290, 246, 200, 26), "Fullscreen", _a.Small);
        _fullscreen = new Toggle(new Rect(624, 244, 56, 28));

        _help = new Label(new Rect(290, 288, 380, 70),
            "Drag the slider or toggle the switch. This help text wraps to the panel width via TextLayout.",
            _a.Small)
        { Wrap = true, Color = new Vector4(0.6f, 0.7f, 0.85f, 1f) };

        _back = new Button(new Rect(390, 372, 180, 50), "Back", _a.Small, ExitScreen);
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
        batch.Draw(_a.White, new Vector4(0, 0, 960, 540), new Vector4(0, 0, 0, 0.55f * TransitionAlpha));   // scrim
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

// Demonstrates the heavy widgets on the 5.x stack: text entry, dropdown, scrollable list
// (scissor-clipped), a hover tooltip, and a modal popup pushed as its own screen.
sealed class WidgetsScreen : Screen
{
    readonly GuiAssets _a;
    Label _title = null!, _nameLabel = null!, _diffLabel = null!, _listLabel = null!;
    TextInput _name = null!;
    Dropdown _difficulty = null!;
    ScrollablePanel _list = null!;
    Tooltip _tip = null!;
    Button _info = null!, _confirm = null!, _back = null!;

    public WidgetsScreen(GuiAssets a) { _a = a; PassUpdateThrough = false; }

    public override void LoadContent()
    {
        _title = new Label(new Rect(0, 28, 960, 40), "Widgets", _a.Big) { Align = TextAlign.Center };

        _nameLabel = new Label(new Rect(120, 92, 260, 18), "Name", _a.Small);
        _name = new TextInput(new Rect(120, 112, 260, 32), _a.Small) { Placeholder = "type a name", MaxLength = 16 };

        _diffLabel = new Label(new Rect(120, 158, 260, 18), "Difficulty", _a.Small);
        _difficulty = new Dropdown(
            new[] { new DropdownOption("Easy", 0), new DropdownOption("Normal", 1), new DropdownOption("Hard", 2) },
            new Rect(120, 178, 180, 30));
        _difficulty.SelectByValue(1);

        _listLabel = new Label(new Rect(120, 222, 260, 18), "Scrollable list (wheel / drag)", _a.Small);
        _list = new ScrollablePanel(new Rect(120, 244, 280, 200)) { ItemCount = 24, ItemHeight = 30, ItemSpacing = 4 };

        _tip = new Tooltip(_a.Small, _a.Small) { Viewport = new Vector2(960, 540) };
        _info = new Button(new Rect(620, 112, 160, 32), "hover for tip", _a.Small);
        _confirm = new Button(new Rect(620, 380, 160, 48), "Confirm…", _a.Small, () => Manager.Add(new PopupScreen(_a, _name.Text, _difficulty.SelectedLabel)));
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
            batch.Draw(_a.White, new Vector4(r.X + 4, r.Y, r.Width - 8, r.Height), new Vector4(0.12f, 0.14f, 0.2f, 1f));
            batch.DrawString(_a.Small, $"Item {i + 1}", new Vector2(r.X + 14, r.Y + (r.Height - _a.Small.LineHeight) * 0.5f), new Vector4(0.8f, 0.84f, 0.9f, 1f));
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

// A modal popup pushed as its own screen, driven by PopupPanel.
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

    public override void LoadContent()
    {
        _popup = new PopupPanel
        {
            Viewport = new Vector2(960, 540),
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
