using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.App;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>The hub root scene: a title + subtitle, a grid of room tiles (each a title over a one-line blurb),
    /// a hint line, and an engine-version footer, drawn through the point-space <see cref="UiViewport"/>
    /// (<see cref="GameScene.OnDrawUi"/>) so its text is baked at the live DPI scale (crisp on HiDPI) and the tiles
    /// wear uniform device-pixel-snapped borders. Each tile is a retained <see cref="Button"/> for chrome +
    /// click, wearing the accent <see cref="GuiStyle.Active"/> preset when selected and the muted
    /// <see cref="GuiStyle.Secondary"/> otherwise; the title + blurb are drawn on top. Arrows / WASD move the
    /// selection spatially over the grid (via <see cref="ShowcaseMenu"/>'s clamped grid moves), Enter/Space or a
    /// click enters a room. The whole layout reflows around the centre on resize rather than magnifying. An empty
    /// registry shows the title chrome with no tiles to enter.</summary>
    public sealed class MenuScene : GameScene
    {
        readonly Texture2D _white;
        readonly DpiFont _titleFont;    // dpi40: the hub title
        readonly DpiFont _tileFont;     // dpi22: each tile's title
        readonly DpiFont _smallFont;    // dpi16: subtitle, blurbs, hint, footer
        readonly IReadOnlyList<ShowcaseRoomEntry> _rooms;
        readonly ShowcaseMenu _menu;
        readonly List<Button> _rows = new();
        readonly string _engineVersion;

        const int Columns = 2;
        const float TileW = 440f, TileH = 76f, Gap = 16f, GridTop = 150f, TitleY = 44f;

        // The Raw("") tile labels are the intentional escape hatch: a tile's real title + blurb are drawn on top
        // through their StringIds, so the button body itself carries no copy.
        [LocalizationExempt]
        public MenuScene(Texture2D white, DpiFont titleFont, DpiFont tileFont, DpiFont smallFont,
                         IReadOnlyList<ShowcaseRoomEntry> rooms)
        {
            _white = white;
            _titleFont = titleFont;
            _tileFont = tileFont;
            _smallFont = smallFont;
            _rooms = rooms;
            _engineVersion = ResolveEngineVersion();

            var names = new List<string>(rooms.Count);
            foreach (ShowcaseRoomEntry r in rooms) names.Add(((LocalizedText)r.Title).Resolve());
            _menu = new ShowcaseMenu(names, Columns);

            SpriteFont seed = tileFont.For(1f);
            for (int i = 0; i < _rooms.Count; i++)
            {
                int index = i;   // capture per-iteration for the click handler
                _rows.Add(new Button(default, LocalizedText.Raw(""), seed, () => Choose(index)));
            }
        }

        // Tiles are laid out and hit-tested in POINT space, so a resized window reflows the grid around the centre
        // without blurring it. The grid is 2 columns wide, centred horizontally; the last row may hold one tile.
        static Rect TileRect(int index, float boundsW)
        {
            float gridW = Columns * TileW + (Columns - 1) * Gap;
            float gridX = (boundsW - gridW) * 0.5f;
            int col = index % Columns, row = index / Columns;
            return new Rect(gridX + col * (TileW + Gap), GridTop + row * (TileH + Gap), TileW, TileH);
        }

        void Layout(float boundsW)
        {
            for (int i = 0; i < _rows.Count; i++) _rows[i].Bounds = TileRect(i, boundsW);
        }

        public override void OnUpdate(float dt)
        {
            var m = Manager!;
            InputState input = m.Input;
            if (input.WasPressed(Key.Up) || input.WasPressed(Key.W)) _menu.MoveUp();
            if (input.WasPressed(Key.Down) || input.WasPressed(Key.S)) _menu.MoveDown();
            if (input.WasPressed(Key.Left) || input.WasPressed(Key.A)) _menu.MoveLeft();
            if (input.WasPressed(Key.Right) || input.WasPressed(Key.D)) _menu.MoveRight();

            float boundsW = m.UiViewport?.Width ?? (m.Viewport?.DesignBounds.Width ?? 0f);
            Layout(boundsW);
            if (m.UiPointer is { } p)
                foreach (Button row in _rows)
                    if (row.Update(p)) return;   // a tile was clicked and entered its room

            if (input.WasPressed(Key.Enter) || input.WasPressed(Key.Space)) Enter(m);
        }

        // A tile's click handler: select it (so the highlight follows the click) and enter it.
        void Choose(int index)
        {
            _menu.SelectAt(index);
            Enter(Manager!);
        }

        void Enter(SceneManager m)
        {
            if (_menu.Selected < 0 || _menu.Selected >= _rooms.Count) return;
            m.Push(_rooms[_menu.Selected].Factory());
        }

        // The hub chrome resolves title/subtitle/hint/footer + every tile title/blurb through the catalog; only
        // the version substituted into the footer format string is a raw token, so no bare copy literal is drawn.
        [LocalizationExempt]
        public override void OnDrawUi(SpriteBatch batch)
        {
            var m = Manager!;
            UiViewport? ui = m.UiViewport;
            if (ui is null) return;   // no point-space viewport wired: nothing to draw

            float dpi = ui.DpiScale;
            SpriteFont titleFont = _titleFont.For(dpi);
            SpriteFont tileFont = _tileFont.For(dpi);
            SpriteFont smallFont = _smallFont.For(dpi);
            float boundsW = ui.Width, boundsH = ui.Height;
            GuiTheme theme = GuiTheme.Default;

            Layout(boundsW);
            batch.Draw(_white, new Vector4(0, 0, boundsW, boundsH), (Color)theme.Background);

            string title = ((LocalizedText)ShowcaseStrings.HubTitle).Resolve();
            Vector2 ts = titleFont.Measure(title);
            batch.DrawString(titleFont, title, new Vector2((boundsW - ts.X) * 0.5f, TitleY), (Color)theme.Text);

            string subtitle = ((LocalizedText)ShowcaseStrings.HubSubtitle).Resolve();
            Vector2 subs = smallFont.Measure(subtitle);
            batch.DrawString(smallFont, subtitle, new Vector2((boundsW - subs.X) * 0.5f, TitleY + titleFont.LineHeight + 6f),
                (Color)theme.TextMuted);

            for (int i = 0; i < _rows.Count; i++)
            {
                Button tile = _rows[i];
                GuiStyle style = i == _menu.Selected ? GuiStyle.Active : GuiStyle.Secondary;
                tile.Font = tileFont;
                tile.Style = style;
                tile.Draw(batch, _white);   // rounded body + device-pixel-snapped border via GuiDraw

                Rect r = tile.Bounds;
                string tileTitle = ((LocalizedText)_rooms[i].Title).Resolve();
                batch.DrawString(tileFont, tileTitle, new Vector2(r.X + 18f, r.Y + 12f), (Color)style.Text);
                string blurb = ((LocalizedText)_rooms[i].Blurb).Resolve();
                Vector4 blurbColor = style.Text; blurbColor.W *= 0.72f;
                batch.DrawString(smallFont, blurb, new Vector2(r.X + 18f, r.Y + 42f), (Color)blurbColor);
            }

            if (_rooms.Count > 0)
            {
                string hint = ((LocalizedText)ShowcaseStrings.HubHint).Resolve();
                Vector2 hs = smallFont.Measure(hint);
                int rows = (_rooms.Count + Columns - 1) / Columns;
                float hy = GridTop + rows * (TileH + Gap) + 6f;
                batch.DrawString(smallFont, hint, new Vector2((boundsW - hs.X) * 0.5f, hy), (Color)theme.TextMuted);
            }

            // Engine-version footer, bottom-right, sitting above the app's display readout band.
            string footer = LocalizedText.Of(ShowcaseStrings.HubEngineVersion, _engineVersion).Resolve();
            Vector2 fs = smallFont.Measure(footer);
            float fy = boundsH - ShowcaseApp.DisplayReadoutHeight - smallFont.LineHeight - 6f;
            batch.DrawString(smallFont, footer, new Vector2(boundsW - fs.X - 16f, fy), (Color)theme.TextMuted);
        }

        // The engine's informational version (the showcase assembly is unversioned; the engine assembly carries
        // <KhaozEngineVersion>), with any '+buildmeta' suffix stripped so the footer reads "KhaozEngine X.Y.Z".
        static string ResolveEngineVersion()
        {
            Assembly engine = typeof(GameApp).Assembly;
            string? v = engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(v)) v = engine.GetName().Version?.ToString() ?? "";
            int plus = v.IndexOf('+');
            return plus >= 0 ? v.Substring(0, plus) : v;
        }
    }
}
