using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Game;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>Ported from <c>Render2DSample/Program.cs</c>: sprites (a solid panel + a checker pattern) and
    /// runtime-baked TTF text, batched through <see cref="SpriteBatch"/>. The room owns no GPU device itself (a
    /// <see cref="GameScene"/> cannot reach one) - <see cref="ShowcaseApp"/> creates the texture/fonts on its
    /// <c>Surface2D</c> and hands them in via <see cref="Init"/> right after construction, keeping the
    /// constructor itself parameterless for the room registry's <c>Func&lt;GameScene&gt;</c> factory.</summary>
    public sealed class Room2D : GameScene
    {
        Texture2D _white = null!;
        Texture2D _checker = null!;
        DpiFont _big = null!;
        DpiFont _small = null!;

        /// <summary>Wire in the textures/fonts created on the app's Surface2D. Call once, right after
        /// construction and before the room is pushed. The captions are drawn through the point-space UI pass, so
        /// they take <see cref="DpiFont"/>s and stay crisp on HiDPI.</summary>
        public Room2D Init(Texture2D white, Texture2D checker, DpiFont big, DpiFont small)
        {
            _white = white;
            _checker = checker;
            _big = big;
            _small = small;
            return this;
        }

        /// <summary>Build the 64x64 checker pattern the sample used to prove textured sprites.</summary>
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

        public override void OnUpdate(float dt)
        {
            if (Manager!.Input.WasPressed(Key.Escape)) { Manager!.Pop(); return; }
        }

        // Demo captions naming the engine features on show - developer-facing chrome, not localizable player
        // copy, so the raw DrawString literals here are intentional (the KELOC003 escape hatch, same idea as the
        // [LocalizationExempt] Gui screens in RoomGui).
        // The textured checker sprites are the design-space "sprite demo" (they letterbox with the game field).
        public override void OnDraw2D(SpriteBatch batch)
        {
            for (int i = 0; i < 6; i++)
            {
                float s = 60 + i * 14;
                batch.Draw(_checker, new Vector4(60 + i * 130, 170, s, s), new Color(1f, 1f, 1f, 1f));
            }
        }

        // The header bar + captions draw through the point-space UI pass so the runtime TTF text is crisp on HiDPI.
        // (At the showcase's default window the point space and the design canvas coincide, so the captions still
        // sit over the sprites above.)
        [LocalizationExempt]
        public override void OnDrawUi(SpriteBatch batch)
        {
            var m = Manager!;
            UiViewport? ui = m.UiViewport;
            if (ui is null) return;
            float dpi = ui.DpiScale;
            SpriteFont big = _big.For(dpi);
            SpriteFont small = _small.For(dpi);
            float boundsW = ui.Width;

            batch.Draw(_white, new Vector4(40, 30, boundsW - 80, 90), new Color(0.18f, 0.22f, 0.30f, 0.92f));
            batch.DrawString(big, "KhaozEngine.Render2D", new Vector2(60, 40), new Color(0.95f, 0.97f, 1f, 1f));
            batch.DrawString(small, "SpriteBatch + Camera2D + Texture2D + runtime TTF text, all on Veldrid.", new Vector2(60, 300), new Color(0.8f, 0.85f, 0.95f, 1f));
            batch.DrawString(small, "The quick brown fox jumps over the lazy dog. 0123456789 !?@#", new Vector2(60, 340), new Color(0.9f, 0.8f, 0.6f, 1f));
            batch.DrawString(small, "Alpha blending, tinting, batched quads. Esc for menu.", new Vector2(60, 380), new Color(0.7f, 0.95f, 0.8f, 1f));
        }
    }
}
