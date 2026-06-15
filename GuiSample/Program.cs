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
    Button _back = null!;

    public SettingsScreen(GuiAssets a)
    {
        _a = a;
        DrawOrder = 10;
        PassUpdateThrough = false;     // modal: the menu beneath neither updates nor receives input
        TransitionOnDuration = 0.18f;
        TransitionOffDuration = 0.18f;
    }

    public override void LoadContent() =>
        _back = new Button(new Rect(390, 320, 180, 56), "Back", _a.Small, ExitScreen);

    public override bool Update(float dt, bool receivesInput)
    {
        if (receivesInput) _back.Update(Manager.Pointer);
        return true;
    }

    public override void Draw(SpriteBatch batch)
    {
        float a = TransitionAlpha;
        batch.Draw(_a.White, new Vector4(0, 0, 960, 540), new Vector4(0, 0, 0, 0.55f * a));            // scrim
        batch.Draw(_a.White, new Vector4(280, 150, 400, 240), new Vector4(0.14f, 0.18f, 0.26f, a));    // panel
        batch.DrawString(_a.Big, "Settings", new Vector2(330, 190), new Vector4(0.95f, 0.97f, 1f, a));
        _back.Draw(batch, _a.White);
    }
}
