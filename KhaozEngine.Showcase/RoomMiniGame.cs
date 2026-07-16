using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Audio;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>Ported from <c>MiniGame/Program.cs</c> ("Catcher": move the paddle to catch falling blocks, miss
    /// three and it's over) into a room hosting its own <see cref="ScreenStack"/> (title -&gt; play -&gt; game
    /// over, same as the sample's title/play/game-over screens). Everything lays out from the live
    /// <see cref="IDesignViewport.DesignBounds"/> now (no 960x540 hardcode), so the field fills whatever design
    /// size the app runs at. The room owns no GPU device itself (a <see cref="GameScene"/> cannot reach one) -
    /// <see cref="ShowcaseApp"/> creates the texture/fonts on its <c>Surface2D</c> and hands them in via
    /// <see cref="Init"/> right after construction, keeping the constructor parameterless for the room registry's
    /// <c>Func&lt;GameScene&gt;</c> factory.
    /// <para>Music: the sample generates a two-second looping arpeggio WAV at runtime (no shipped asset file) and
    /// plays it through <see cref="AudioSystem"/> as looped music. This room does the same, generating and loading
    /// the track in <see cref="OnEnter"/> (playback then auto-starts and loops the first time
    /// <see cref="AudioSystem.Update()"/> runs) and disposing the <see cref="AudioSystem"/> in <see cref="OnExit"/>
    /// so leaving the room silences it.</para>
    /// <para>Key remap vs the sample: the sample used Escape to close the whole window. Here Escape returns to the
    /// showcase menu once the room's own screen stack is back down to just the title screen (mirrors the GUI room's
    /// Esc-pops-topmost-first convention).</para></summary>
    public sealed class RoomMiniGame : GameScene, IShowcaseRoom
    {
        static readonly StringId[] Hints = { ShowcaseStrings.ControlsMiniGame };

        MiniGameCtx _ctx = null!;
        ScreenStack _stack = null!;
        AudioSystem _audio = null!;

        public StringId Title => ShowcaseStrings.RoomMiniGameTitle;
        public IReadOnlyList<StringId> ControlsHints => Hints;

        /// <summary>Wire in the texture/fonts created on the app's Surface2D. Call once, right after
        /// construction and before the room is pushed.</summary>
        public RoomMiniGame Init(Texture2D white, SpriteFont big, SpriteFont small)
        {
            _ctx = new MiniGameCtx(white, big, small, new Random());
            return this;
        }

        public override void OnEnter()
        {
            // Background music: generate the same short looping WAV the sample used and play it through the OpenAL
            // backend (falls back to a silent backend headless, so this never crashes the room). A single
            // registered track under the default PlayMode.RandomRotation just replays itself when it ends, i.e. it
            // loops. Playback is driven by AudioSystem.Update() (called every OnUpdate below), auto-starting on its
            // first call.
            string musicDir = Path.Combine(Path.GetTempPath(), "ke-showcase-minigame");
            Directory.CreateDirectory(musicDir);
            WriteLoopWav(Path.Combine(musicDir, "theme.wav"));
            _audio = new AudioSystem(new[] { "theme" }) { MusicVolume = 0.35f };
            _audio.LoadContent(musicDir);

            _stack = new ScreenStack();
            _stack.Add(new MiniGameTitleScreen(_ctx, Manager!.Viewport!));
        }

        // Disposing the AudioSystem tears down the music backend directly, silencing playback immediately - the
        // same teardown MiniGame/Program.cs does on window close.
        public override void OnExit() => _audio.Dispose();

        public override void OnUpdate(float dt)
        {
            var m = Manager!;
            // Esc at the title/play screen (nothing modal on top) leaves the room straight to the showcase menu.
            if (m.Input.WasPressed(Key.Escape) && _stack.Screens.Count <= 1) { m.Pop(); return; }
            _audio.Update();
            _stack.Update(dt, m.Input, m.Viewport);
            // A "Back to menu" button (on the title or the game-over screen) exits its screen, which can leave the
            // room's own stack empty. With nothing left to draw, leave the room so the showcase menu shows again
            // instead of an empty (cleared) frame. Pop is deferred by the SceneManager, so this is safe mid-update.
            if (_stack.Screens.Count == 0) m.Pop();
        }

        public override void OnDraw2D(SpriteBatch batch) => _stack.Draw(batch);

        // Two seconds of a simple looping arpeggio (16-bit mono @ 44100), identical recipe to MiniGame/Program.cs.
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
    }

    /// <summary>Shared white texture, two font sizes, and the RNG, mirroring <c>MiniGame</c>'s <c>GameCtx</c>.
    /// The room's Quit buttons pop back to the showcase menu (see <see cref="MiniGameTitleScreen"/>/
    /// <see cref="MiniGameGameOverScreen"/>).</summary>
    sealed class MiniGameCtx
    {
        public readonly Texture2D White;
        public readonly SpriteFont Big, Small;
        public readonly Random Rng;
        public MiniGameCtx(Texture2D white, SpriteFont big, SpriteFont small, Random rng)
        { White = white; Big = big; Small = small; Rng = rng; }

        public void Rect(SpriteBatch b, float x, float y, float w, float h, Vector4 c) => b.Draw(White, new Vector4(x, y, w, h), (Color)c);
    }

    sealed class MiniGameTitleScreen : Screen
    {
        readonly MiniGameCtx _c;
        readonly IDesignViewport _vp;
        Button _start = null!, _quit = null!;
        public MiniGameTitleScreen(MiniGameCtx c, IDesignViewport vp) { _c = c; _vp = vp; PassUpdateThrough = false; BackgroundColor = GuiTheme.Default.Background; }

        public override void LoadContent()
        {
            Rect db = _vp.DesignBounds;
            float cx = (db.Width - 200f) * 0.5f, cy = db.Height * 0.5f;
            _start = new Button(new Rect(cx, cy, 200f, 56f), ShowcaseStrings.MiniGamePlay, _c.Small,
                () => { Manager.Remove(this); Manager.Add(new MiniGamePlayScreen(_c, _vp)); });
            _quit = new Button(new Rect(cx, cy + 72f, 200f, 56f), ShowcaseStrings.MiniGameBackToMenu, _c.Small, ExitScreen);
        }

        public override bool Update(float dt, bool receivesInput)
        {
            if (!receivesInput) return false;
            _start.Update(Manager.Pointer); _quit.Update(Manager.Pointer);
            return true;
        }

        public override void Draw(SpriteBatch b)
        {
            DrawBackground(b, _c.White, _vp);
            Rect db = _vp.DesignBounds;
            string title = ((LocalizedText)ShowcaseStrings.MiniGameTitle).Resolve();
            Vector2 ts = _c.Big.Measure(title);
            b.DrawString(_c.Big, title, new Vector2((db.Width - ts.X) * 0.5f, db.Height * 0.24f), (Color)GuiTheme.Default.Text);
            string tag = ((LocalizedText)ShowcaseStrings.MiniGameTagline).Resolve();
            Vector2 gs = _c.Small.Measure(tag);
            b.DrawString(_c.Small, tag, new Vector2((db.Width - gs.X) * 0.5f, db.Height * 0.34f), (Color)GuiTheme.Default.TextMuted);
            _start.Draw(b, _c.White); _quit.Draw(b, _c.White);
        }
    }

    sealed class MiniGamePlayScreen : Screen
    {
        struct Item { public Vector2 Pos; public float Speed; public Vector4 Color; }

        readonly MiniGameCtx _c;
        readonly IDesignViewport _vp;
        readonly List<Item> _items = new();
        float _paddleX = 420, _spawn;
        int _score, _lives = 3;
        const float PaddleW = 120, PaddleH = 18, ItemSize = 22;

        // The paddle rides a fixed clearance above the bottom edge, so it tracks the live design height.
        static float PaddleY(Rect db) => db.Height - 88f;

        public MiniGamePlayScreen(MiniGameCtx c, IDesignViewport vp) { _c = c; _vp = vp; PassUpdateThrough = false; BackgroundColor = GuiTheme.Default.Background; }

        public override bool Update(float dt, bool receivesInput)
        {
            if (!receivesInput) return true;   // frozen under the game-over modal
            Rect db = _vp.DesignBounds;
            float w = db.Width, h = db.Height, paddleY = PaddleY(db);
            var input = Manager.Input;
            float speed = 520f * dt;
            if (input.IsDown(Key.Left) || input.IsDown(Key.A)) _paddleX -= speed;
            if (input.IsDown(Key.Right) || input.IsDown(Key.D)) _paddleX += speed;
            _paddleX = Math.Clamp(_paddleX, 0, w - PaddleW);

            _spawn -= dt;
            if (_spawn <= 0f)
            {
                _spawn = Math.Max(0.35f, 0.9f - _score * 0.015f);
                _items.Add(new Item
                {
                    Pos = new Vector2(_c.Rng.Next(20, (int)MathF.Max(21f, w - 40f)), -ItemSize),
                    Speed = 170 + _c.Rng.Next(0, 120) + _score * 4,
                    Color = new Vector4(0.95f, 0.6f + _c.Rng.NextSingle() * 0.3f, 0.25f, 1f),
                });
            }

            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var it = _items[i];
                it.Pos.Y += it.Speed * dt;
                bool caught = it.Pos.Y + ItemSize >= paddleY && it.Pos.Y <= paddleY + PaddleH
                              && it.Pos.X + ItemSize >= _paddleX && it.Pos.X <= _paddleX + PaddleW;
                if (caught) { _score++; _items.RemoveAt(i); }
                else if (it.Pos.Y > h) { _lives--; _items.RemoveAt(i); }
                else _items[i] = it;
            }

            if (_lives <= 0) Manager.Add(new MiniGameGameOverScreen(_c, _vp, this, _score));
            return true;
        }

        public override void Draw(SpriteBatch b)
        {
            DrawBackground(b, _c.White, _vp);
            Rect db = _vp.DesignBounds;
            float paddleY = PaddleY(db);
            foreach (var it in _items) _c.Rect(b, it.Pos.X, it.Pos.Y, ItemSize, ItemSize, it.Color);
            _c.Rect(b, _paddleX, paddleY, PaddleW, PaddleH, new Vector4(0.5f, 0.85f, 0.95f, 1f));

            string score = LocalizedText.Of(ShowcaseStrings.MiniGameScore, _score).Resolve();
            b.DrawString(_c.Small, score, new Vector2(20, 16), (Color)GuiTheme.Default.Text);
            string lives = LocalizedText.Of(ShowcaseStrings.MiniGameLives, _lives).Resolve();
            Vector2 ls = _c.Small.Measure(lives);
            b.DrawString(_c.Small, lives, new Vector2(db.Width - ls.X - 20f, 16), (Color)GuiTheme.Default.TextMuted);
        }
    }

    sealed class MiniGameGameOverScreen : Screen
    {
        readonly MiniGameCtx _c;
        readonly IDesignViewport _vp;
        readonly MiniGamePlayScreen _play;
        readonly int _score;
        Rect _dialog;
        Button _retry = null!, _quit = null!;

        public MiniGameGameOverScreen(MiniGameCtx c, IDesignViewport vp, MiniGamePlayScreen play, int score)
        {
            _c = c; _vp = vp; _play = play; _score = score;
            DrawOrder = 10; PassUpdateThrough = false;          // modal: freezes the play screen beneath
            TransitionOnDuration = 0.2f;
        }

        public override void LoadContent()
        {
            _dialog = Layout.Resolve(_vp.DesignBounds, Anchor.Center, 440, 250);
            _retry = new Button(new Rect(_dialog.X + 40f, _dialog.Y + 170f, 160f, 54f), ShowcaseStrings.MiniGameRetry, _c.Small,
                () => { Manager.Remove(_play); Manager.Remove(this); Manager.Add(new MiniGamePlayScreen(_c, _vp)); });
            _quit = new Button(new Rect(_dialog.Right - 200f, _dialog.Y + 170f, 160f, 54f), ShowcaseStrings.MiniGameBackToMenu, _c.Small,
                () => { Manager.Remove(_play); ExitScreen(); });
        }

        public override bool Update(float dt, bool receivesInput)
        {
            if (receivesInput) { _retry.Update(Manager.Pointer); _quit.Update(Manager.Pointer); }
            return true;
        }

        public override void Draw(SpriteBatch b)
        {
            float a = TransitionAlpha;
            Rect db = _vp.DesignBounds;
            // Crisp-theme dialog chrome, all faded by the screen's transition alpha.
            Vector4 surf = GuiTheme.Default.Surface, text = GuiTheme.Default.Text, muted = GuiTheme.Default.TextMuted;
            _c.Rect(b, db.X, db.Y, db.Width, db.Height, new Vector4(0, 0, 0, 0.6f * a));
            _c.Rect(b, _dialog.X, _dialog.Y, _dialog.Width, _dialog.Height, new Vector4(surf.X, surf.Y, surf.Z, a));

            string over = ((LocalizedText)ShowcaseStrings.MiniGameGameOver).Resolve();
            Vector2 os = _c.Big.Measure(over);
            b.DrawString(_c.Big, over, new Vector2(_dialog.X + (_dialog.Width - os.X) * 0.5f, _dialog.Y + 40f), new Color(text.X, text.Y, text.Z, a));
            string final = LocalizedText.Of(ShowcaseStrings.MiniGameFinalScore, _score).Resolve();
            Vector2  fs = _c.Small.Measure(final);
            b.DrawString(_c.Small, final, new Vector2(_dialog.X + (_dialog.Width - fs.X) * 0.5f, _dialog.Y + 110f), new Color(muted.X, muted.Y, muted.Z, a));
            _retry.Draw(b, _c.White); _quit.Draw(b, _c.White);
        }
    }
}
