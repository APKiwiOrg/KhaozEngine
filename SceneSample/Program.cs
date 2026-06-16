using System;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

// Minimal SceneManager demo on the 5.x stack: a GameApp holds a SceneManager and forwards the loop to it.
// Two scenes prove push/switch/overlay/pop live:
//   MenuScene  - press a key / click -> SwitchTo(PlayScene)   (hard switch, clears the stack)
//   PlayScene  - Esc -> Push(PauseScene) as a transparent overlay; Esc again -> Pop() back to play.
// The PauseScene is DrawBelow=true (the frozen game shows through) and UpdateBelow=false (the game freezes).
// Honors KE_MAX_FRAMES via the AppWindow loop, so a headless smoke run renders N frames then exits 0.
using var app = new SceneSampleApp();
app.Run();
return 0;

// A GameApp that owns a SceneManager. The per-frame context (Input/Pointer/Viewport/FrameWidth/FrameHeight)
// is copied into the manager before Update, exactly as the spec's intended wiring shows.
sealed class SceneSampleApp : GameApp
{
    readonly SceneManager _scenes = new();
    Texture2D _white = null!;

    public SceneSampleApp() : base(GameAppOptions.For("KhaozEngine - SceneManager demo", 960, 540)) { }

    protected override void OnLoad()
    {
        // 1x1 white texture so scenes can paint solid colour panels without a font/asset dependency.
        _white = Surface2D.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
        _scenes.Push(new MenuScene(_white));
    }

    protected override void OnUpdate(float dt)
    {
        _scenes.Input = Input;
        _scenes.Pointer = Pointer;
        _scenes.Viewport = Viewport;
        _scenes.FrameWidth = FrameWidth;
        _scenes.FrameHeight = FrameHeight;
        _scenes.Update(dt);
    }

    protected override void OnDraw2D(SpriteBatch batch) => _scenes.Draw2D(batch);
    protected override void OnResize(int w, int h) => _scenes.Resize(w, h);
}

// Press any key or click -> hard switch to the play scene.
sealed class MenuScene : GameScene
{
    readonly Texture2D _white;
    public MenuScene(Texture2D white) => _white = white;

    public override void OnEnter() => Console.WriteLine("[scene] Menu enter");
    public override void OnExit() => Console.WriteLine("[scene] Menu exit");

    public override void OnUpdate(float dt)
    {
        var m = Manager!;
        bool start = m.Input.WasPressed(Key.Enter) || m.Input.WasPressed(Key.Space)
            || (m.Pointer?.IsJustReleased ?? false);
        if (start) m.SwitchTo(new PlayScene(_white));
    }

    public override void OnDraw2D(SpriteBatch batch)
    {
        var m = Manager!;
        // Dim full-frame panel as a menu backdrop.
        batch.Draw(_white, new Vector4(0, 0, m.FrameWidth, m.FrameHeight), new Vector4(0.12f, 0.16f, 0.24f, 1f));
        batch.Draw(_white, new Vector4(m.FrameWidth * 0.3f, m.FrameHeight * 0.42f, m.FrameWidth * 0.4f, 40),
            new Vector4(0.3f, 0.6f, 0.9f, 1f));
    }
}

// Esc -> push a pause overlay; Esc again -> pop it.
sealed class PlayScene : GameScene
{
    readonly Texture2D _white;
    public PlayScene(Texture2D white) => _white = white;

    public override void OnEnter() => Console.WriteLine("[scene] Play enter");
    public override void OnExit() => Console.WriteLine("[scene] Play exit");

    public override void OnUpdate(float dt)
    {
        var m = Manager!;
        if (m.Input.WasPressed(Key.Escape)) m.Push(new PauseScene(_white));
    }

    public override void OnDraw2D(SpriteBatch batch)
    {
        var m = Manager!;
        batch.Draw(_white, new Vector4(0, 0, m.FrameWidth, m.FrameHeight), new Vector4(0.08f, 0.2f, 0.12f, 1f));
        batch.Draw(_white, new Vector4(m.FrameWidth * 0.45f, m.FrameHeight * 0.45f, 60, 60),
            new Vector4(0.9f, 0.8f, 0.2f, 1f));
    }
}

// A transparent overlay: the frozen play scene draws below it (DrawBelow) and does not update (UpdateBelow=false).
sealed class PauseScene : GameScene
{
    readonly Texture2D _white;
    public PauseScene(Texture2D white)
    {
        _white = white;
        DrawBelow = true;   // let the frozen game show through
        UpdateBelow = false; // freeze the game while paused
    }

    public override void OnEnter() => Console.WriteLine("[scene] Pause enter (overlay)");
    public override void OnExit() => Console.WriteLine("[scene] Pause exit");

    public override void OnUpdate(float dt)
    {
        var m = Manager!;
        if (m.Input.WasPressed(Key.Escape)) m.Pop();
    }

    public override void OnDraw2D(SpriteBatch batch)
    {
        var m = Manager!;
        // Semi-transparent scrim over the (still-drawn) play scene + a pause bar.
        batch.Draw(_white, new Vector4(0, 0, m.FrameWidth, m.FrameHeight), new Vector4(0f, 0f, 0f, 0.5f));
        batch.Draw(_white, new Vector4(m.FrameWidth * 0.35f, m.FrameHeight * 0.46f, m.FrameWidth * 0.3f, 32),
            new Vector4(0.9f, 0.9f, 0.95f, 1f));
    }
}
