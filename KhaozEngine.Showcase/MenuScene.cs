using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>The hub root scene: a title, one row button per registered room, and a hint line. Up/Down (or W/S)
    /// move the highlight; Enter/Space or a click enters that room. Drawn through the point-space
    /// <see cref="UiViewport"/> (<see cref="GameScene.OnDrawUi"/>), so its text is baked at the live DPI scale
    /// (crisp on HiDPI) and the row buttons wear uniform device-pixel-snapped borders; the layout reflows around
    /// the centre as the window resizes rather than magnifying. An empty registry shows nothing to enter.</summary>
    // The title, hint, and room-name captions are developer-facing hub chrome (room names are raw literals in
    // ShowcaseApp's registry), not localizable player copy, so the raw captions here are the intentional escape hatch.
    [LocalizationExempt]
    public sealed class MenuScene : GameScene
    {
        readonly Texture2D _white;
        readonly DpiFont _titleFont;
        readonly DpiFont _rowFont;
        readonly IReadOnlyList<(string Name, Func<GameScene> Factory)> _rooms;
        readonly ShowcaseMenu _menu;
        readonly List<Button> _rows = new();

        const float RowH = 48f, RowW = 360f, Top = 150f;

        public MenuScene(Texture2D white, DpiFont titleFont, DpiFont rowFont,
                         IReadOnlyList<(string Name, Func<GameScene> Factory)> rooms)
        {
            _white = white;
            _titleFont = titleFont;
            _rowFont = rowFont;
            _rooms = rooms;
            var names = new List<string>(rooms.Count);
            foreach (var r in rooms) names.Add(r.Name);
            _menu = new ShowcaseMenu(names);

            // One retained button per room: a click selects that row and enters it (the same path as Enter on the
            // keyboard). The font is (re)assigned from the DpiFont each frame in OnDrawUi (the atlas re-bakes only on
            // a DPI change); bounds are laid out from the point-space width each frame.
            SpriteFont seed = rowFont.For(1f);
            for (int i = 0; i < _rooms.Count; i++)
            {
                int index = i;   // capture per-iteration for the click handler
                _rows.Add(new Button(default, LocalizedText.Raw(_rooms[i].Name), seed, () => Choose(index)));
            }
        }

        // Rows are laid out and hit-tested in POINT space (the logical width the UI batch draws in and the UiPointer
        // maps into), so a resized window reflows the menu around the centre without blurring it.
        static Rect RowRect(int i, float boundsW) => new Rect(boundsW * 0.5f - RowW * 0.5f, Top + i * (RowH + 8f), RowW, RowH);

        void Layout(float boundsW)
        {
            for (int i = 0; i < _rows.Count; i++) _rows[i].Bounds = RowRect(i, boundsW);
        }

        public override void OnUpdate(float dt)
        {
            var m = Manager!;
            if (m.Input.WasPressed(Key.Down) || m.Input.WasPressed(Key.S)) _menu.MoveNext();
            if (m.Input.WasPressed(Key.Up) || m.Input.WasPressed(Key.W)) _menu.MovePrev();

            // Lay the rows out for the current point-space width, then drive them off the UI pointer: a tap on a row
            // enters its room (Choose), the same as Enter on that selection.
            float boundsW = m.UiViewport?.Width ?? (m.Viewport?.DesignBounds.Width ?? 0f);
            Layout(boundsW);
            if (m.UiPointer is { } p)
                foreach (var row in _rows)
                    if (row.Update(p)) return;   // a row was clicked and entered its room; nothing more this frame

            if (m.Input.WasPressed(Key.Enter) || m.Input.WasPressed(Key.Space)) Enter(m);
        }

        // A row's click handler: select it (so the highlight follows the click) and enter it.
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

        public override void OnDrawUi(SpriteBatch batch)
        {
            var m = Manager!;
            UiViewport? ui = m.UiViewport;
            if (ui is null) return;   // no point-space viewport wired: nothing to draw

            float dpi = ui.DpiScale;
            SpriteFont titleFont = _titleFont.For(dpi);   // baked at the device density, re-baked only on a DPI change
            SpriteFont rowFont = _rowFont.For(dpi);
            float boundsW = ui.Width, boundsH = ui.Height;

            Layout(boundsW);
            batch.Draw(_white, new Vector4(0, 0, boundsW, boundsH), new Color(0.10f, 0.13f, 0.20f, 1f));

            const string title = "KhaozEngine Showcase";
            Vector2 ts = titleFont.Measure(title);
            batch.DrawString(titleFont, title, new Vector2((boundsW - ts.X) * 0.5f, 60f), new Color(0.92f, 0.95f, 1f, 1f));

            for (int i = 0; i < _rows.Count; i++)
            {
                Button row = _rows[i];
                row.Font = rowFont;                     // point this frame's DPI-baked atlas
                row.Selected = i == _menu.Selected;     // the highlighted row draws in the selected palette
                row.Draw(batch, _white);                // rounded body + device-pixel-snapped border via GuiDraw
            }

            if (_rooms.Count > 0)
            {
                const string hint = "Up/Down and Enter, or click a room. Esc leaves a room.";
                Vector2 hs = rowFont.Measure(hint);
                float hy = Top + _rooms.Count * (RowH + 8f) + 24f;
                batch.DrawString(rowFont, hint, new Vector2((boundsW - hs.X) * 0.5f, hy), new Color(0.5f, 0.58f, 0.7f, 1f));
            }
        }
    }
}
