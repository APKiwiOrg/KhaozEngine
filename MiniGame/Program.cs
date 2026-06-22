using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Audio;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

// A tiny but complete game on the custom 5.x stack — proves Windowing + Render2D + Gui + Audio run a real
// game loop with no MonoGame. "Catcher": move the paddle to catch falling blocks; miss three and it's over.
const int W = 960, H = 540;
var window = new AppWindow("KhaozEngine — Catcher (5.x stack demo)", W, H) { ClearColor = new Color(0.06f, 0.08f, 0.12f, 1f) };
var surface = new Render2DSurface(window);

// Background music: generate a short looping WAV and play it through the OpenAL backend.
string musicDir = Path.Combine(Path.GetTempPath(), "ke-minigame");
Directory.CreateDirectory(musicDir);
WriteLoopWav(Path.Combine(musicDir, "theme.wav"));
var audio = new AudioSystem(new[] { "theme" }) { MusicVolume = 0.35f };
audio.LoadContent(musicDir);

var ctx = new GameCtx(
    window,
    surface.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1),
    surface.LoadDefaultFont(56f),
    surface.LoadDefaultFont(26f),
    new Random());

var stack = new ScreenStack();
stack.Add(Array.IndexOf(args, "--play") >= 0 ? new PlayScreen(ctx) : new TitleScreen(ctx));

window.Run(frame =>
{
    if (frame.Input.WasPressed(Key.Escape)) window.Close();
    audio.Update();
    stack.Update(frame.Dt, frame.Input);

    surface.NewFrame(frame);
    surface.Batch.Begin();
    stack.Draw(surface.Batch);
    surface.Batch.End();
});

audio.Dispose();
surface.Dispose();
window.Dispose();

// Two seconds of a simple looping arpeggio (16-bit mono @ 44100).
static void WriteLoopWav(string path)
{
    int rate = 44100, n = (int)(rate * 2.0);
    float[] notes = { 261.63f, 329.63f, 392.00f, 523.25f, 392.00f, 329.63f }; // C E G C G E
    using var bw = new BinaryWriter(new FileStream(path, FileMode.Create));
    int dataBytes = n * 2;
    bw.Write(new[] { 'R', 'I', 'F', 'F' }); bw.Write(36 + dataBytes); bw.Write(new[] { 'W', 'A', 'V', 'E' });
    bw.Write(new[] { 'f', 'm', 't', ' ' }); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
    bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
    bw.Write(new[] { 'd', 'a', 't', 'a' }); bw.Write(dataBytes);
    for (int i = 0; i < n; i++)
    {
        double t = (double)i / rate;
        int slot = (int)(t / (2.0 / notes.Length)) % notes.Length;
        double env = 0.25 * (0.6 + 0.4 * Math.Sin(t * Math.PI / 2));
        bw.Write((short)(env * short.MaxValue * Math.Sin(2 * Math.PI * notes[slot] * t)));
    }
}

sealed class GameCtx
{
    public readonly AppWindow Window;
    public readonly Texture2D White;
    public readonly SpriteFont Big, Small;
    public readonly Random Rng;
    public GameCtx(AppWindow w, Texture2D white, SpriteFont big, SpriteFont small, Random rng)
    { Window = w; White = white; Big = big; Small = small; Rng = rng; }

    public void Rect(SpriteBatch b, float x, float y, float w, float h, Vector4 c) => b.Draw(White, new Vector4(x, y, w, h), (Color)c);
}

sealed class TitleScreen : Screen
{
    readonly GameCtx _c;
    Button _start = null!, _quit = null!;
    public TitleScreen(GameCtx c) { _c = c; }

    public override void LoadContent()
    {
        _start = new Button(new Rect(380, 280, 200, 56), "Play", _c.Small, () => { Manager.Remove(this); Manager.Add(new PlayScreen(_c)); });
        _quit = new Button(new Rect(380, 350, 200, 56), "Quit", _c.Small, () => _c.Window.Close());
    }
    public override bool Update(float dt, bool receivesInput)
    {
        if (!receivesInput) return false;
        _start.Update(Manager.Pointer); _quit.Update(Manager.Pointer);
        return true;
    }
    public override void Draw(SpriteBatch b)
    {
        b.DrawString(_c.Big, "CATCHER", new Vector2(330, 140), new Color(0.95f, 0.97f, 1f, 1f));
        b.DrawString(_c.Small, "catch the falling blocks — A/D or arrows", new Vector2(290, 220), new Color(0.6f, 0.72f, 0.9f, 1f));
        _start.Draw(b, _c.White); _quit.Draw(b, _c.White);
    }
}

sealed class PlayScreen : Screen
{
    struct Item { public Vector2 Pos; public float Speed; public Vector4 Color; }

    readonly GameCtx _c;
    readonly List<Item> _items = new();
    float _paddleX = 420, _spawn;
    int _score, _lives = 3;
    const float PaddleY = 492, PaddleW = 120, PaddleH = 18, ItemSize = 22;

    public PlayScreen(GameCtx c) { _c = c; }

    public override bool Update(float dt, bool receivesInput)
    {
        if (!receivesInput) return true;   // frozen under the game-over modal
        var input = Manager.Input;
        float speed = 520f * dt;
        if (input.IsDown(Key.Left) || input.IsDown(Key.A)) _paddleX -= speed;
        if (input.IsDown(Key.Right) || input.IsDown(Key.D)) _paddleX += speed;
        _paddleX = Math.Clamp(_paddleX, 0, 960 - PaddleW);

        _spawn -= dt;
        if (_spawn <= 0f)
        {
            _spawn = Math.Max(0.35f, 0.9f - _score * 0.015f);
            _items.Add(new Item { Pos = new Vector2(_c.Rng.Next(20, 920), -ItemSize), Speed = 170 + _c.Rng.Next(0, 120) + _score * 4, Color = new Vector4(0.95f, 0.6f + _c.Rng.NextSingle() * 0.3f, 0.25f, 1f) });
        }

        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var it = _items[i];
            it.Pos.Y += it.Speed * dt;
            bool caught = it.Pos.Y + ItemSize >= PaddleY && it.Pos.Y <= PaddleY + PaddleH
                          && it.Pos.X + ItemSize >= _paddleX && it.Pos.X <= _paddleX + PaddleW;
            if (caught) { _score++; _items.RemoveAt(i); }
            else if (it.Pos.Y > 540) { _lives--; _items.RemoveAt(i); }
            else _items[i] = it;
        }

        if (_lives <= 0) Manager.Add(new GameOverScreen(_c, this, _score));
        return true;
    }

    public override void Draw(SpriteBatch b)
    {
        foreach (var it in _items) _c.Rect(b, it.Pos.X, it.Pos.Y, ItemSize, ItemSize, it.Color);
        _c.Rect(b, _paddleX, PaddleY, PaddleW, PaddleH, new Vector4(0.5f, 0.85f, 0.95f, 1f));
        b.DrawString(_c.Small, $"Score {_score}", new Vector2(20, 16), new Color(0.92f, 0.96f, 1f, 1f));
        b.DrawString(_c.Small, $"Lives {_lives}", new Vector2(820, 16), new Color(1f, 0.7f, 0.6f, 1f));
    }
}

sealed class GameOverScreen : Screen
{
    readonly GameCtx _c;
    readonly PlayScreen _play;
    readonly int _score;
    Button _retry = null!, _quit = null!;

    public GameOverScreen(GameCtx c, PlayScreen play, int score)
    {
        _c = c; _play = play; _score = score;
        DrawOrder = 10; PassUpdateThrough = false;          // modal: freezes the play screen beneath
        TransitionOnDuration = 0.2f;
    }
    public override void LoadContent()
    {
        _retry = new Button(new Rect(300, 320, 160, 54), "Retry", _c.Small, () => { Manager.Remove(_play); Manager.Remove(this); Manager.Add(new PlayScreen(_c)); });
        _quit = new Button(new Rect(500, 320, 160, 54), "Quit", _c.Small, () => _c.Window.Close());
    }
    public override bool Update(float dt, bool receivesInput)
    {
        if (receivesInput) { _retry.Update(Manager.Pointer); _quit.Update(Manager.Pointer); }
        return true;
    }
    public override void Draw(SpriteBatch b)
    {
        float a = TransitionAlpha;
        _c.Rect(b, 0, 0, 960, 540, new Vector4(0, 0, 0, 0.6f * a));
        _c.Rect(b, 260, 150, 440, 250, new Vector4(0.14f, 0.16f, 0.24f, a));
        b.DrawString(_c.Big, "GAME OVER", new Vector2(300, 185), new Color(1f, 0.95f, 0.95f, a));
        b.DrawString(_c.Small, $"Final score: {_score}", new Vector2(380, 265), new Color(0.85f, 0.9f, 1f, a));
        _retry.Draw(b, _c.White); _quit.Draw(b, _c.White);
    }
}
