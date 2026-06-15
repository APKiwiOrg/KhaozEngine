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
    Button _settings = null!, _quitBtn = null!;

    public MenuScreen(GuiAssets a, Action quit) { _a = a; _quit = quit; PassUpdateThrough = false; }

    public override void LoadContent()
    {
        _settings = new Button(new Rect(380, 220, 200, 56), "Settings", _a.Small, () => Manager.Add(new SettingsScreen(_a)));
        _quitBtn = new Button(new Rect(380, 300, 200, 56), "Quit", _a.Small, _quit);
    }

    public override bool Update(float dt, bool receivesInput)
    {
        if (!receivesInput) return false;
        _settings.Update(Manager.Pointer);
        _quitBtn.Update(Manager.Pointer);
        return true;
    }

    public override void Draw(SpriteBatch batch)
    {
        batch.DrawString(_a.Big, "Main Menu", new Vector2(380, 130), new Vector4(0.95f, 0.97f, 1f, 1f));
        _settings.Draw(batch, _a.White);
        _quitBtn.Draw(batch, _a.White);
        batch.DrawString(_a.Small, "click Settings to push a modal screen  •  Esc to quit", new Vector2(180, 470), new Vector4(0.6f, 0.7f, 0.85f, 1f));
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
