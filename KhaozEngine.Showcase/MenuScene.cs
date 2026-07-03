using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>The hub root scene: a title, one labelled row per registered room, and a hint line. Up/Down (or
    /// W/S) move the highlight, Enter/Space or a click on a row enters that room. An empty registry shows nothing
    /// to enter (used only before rooms are registered).</summary>
    public sealed class MenuScene : GameScene
    {
        readonly Texture2D _white;
        readonly SpriteFont _titleFont;
        readonly SpriteFont _rowFont;
        readonly IReadOnlyList<(string Name, Func<GameScene> Factory)> _rooms;
        readonly ShowcaseMenu _menu;

        const float RowH = 48f, RowW = 360f, Top = 150f;

        public MenuScene(Texture2D white, SpriteFont titleFont, SpriteFont rowFont,
                         IReadOnlyList<(string Name, Func<GameScene> Factory)> rooms)
        {
            _white = white;
            _titleFont = titleFont;
            _rowFont = rowFont;
            _rooms = rooms;
            var names = new List<string>(rooms.Count);
            foreach (var r in rooms) names.Add(r.Name);
            _menu = new ShowcaseMenu(names);
        }

        // Rows are laid out and hit-tested in DESIGN space (the width the SpriteBatch draws in and the Pointer
        // maps into), never window/frame pixels - otherwise a resized window pushes the menu off-centre because
        // the design-space batch and pointer disagree with a frame-pixel layout.
        static Rect RowRect(int i, float boundsW) => new Rect(boundsW * 0.5f - RowW * 0.5f, Top + i * (RowH + 8f), RowW, RowH);

        public override void OnUpdate(float dt)
        {
            var m = Manager!;
            if (m.Input.WasPressed(Key.Down) || m.Input.WasPressed(Key.S)) _menu.MoveNext();
            if (m.Input.WasPressed(Key.Up) || m.Input.WasPressed(Key.W)) _menu.MovePrev();

            // Click a row: hit-test each row rect with the pointer press-origin helper (design-space bounds).
            float boundsW = m.Viewport!.DesignBounds.Width;
            if (m.Pointer is { } p && p.IsJustReleased)
                for (int i = 0; i < _rooms.Count; i++)
                    if (p.IsTapIn(RowRect(i, boundsW))) { _menu.SelectAt(i); Enter(m); return; }

            if (m.Input.WasPressed(Key.Enter) || m.Input.WasPressed(Key.Space)) Enter(m);
        }

        void Enter(SceneManager m)
        {
            if (_menu.Selected < 0 || _menu.Selected >= _rooms.Count) return;
            m.Push(_rooms[_menu.Selected].Factory());
        }

        public override void OnDraw2D(SpriteBatch batch)
        {
            var m = Manager!;
            // Everything below is drawn in design space (SpriteBatch.Begin(viewport) is already active), so centre
            // on the design bounds, not FrameWidth/FrameHeight - those are window pixels and drift on resize.
            Rect db = m.Viewport!.DesignBounds;
            float frameW = db.Width;
            batch.Draw(_white, new Vector4(0, 0, frameW, db.Height), new Color(0.10f, 0.13f, 0.20f, 1f));

            const string title = "KhaozEngine Showcase";
            Vector2 ts = _titleFont.Measure(title);
            batch.DrawString(_titleFont, title, new Vector2((frameW - ts.X) * 0.5f, 60f), new Color(0.92f, 0.95f, 1f, 1f));

            for (int i = 0; i < _rooms.Count; i++)
            {
                bool sel = i == _menu.Selected;
                Rect r = RowRect(i, frameW);
                batch.Draw(_white, new Vector4(r.X, r.Y, r.Width, r.Height),
                    sel ? new Color(0.30f, 0.55f, 0.85f, 1f) : new Color(0.20f, 0.24f, 0.32f, 1f));

                string name = _rooms[i].Name;
                Vector2 sz = _rowFont.Measure(name);
                var pos = new Vector2(r.X + (r.Width - sz.X) * 0.5f, r.Y + (r.Height - sz.Y) * 0.5f);
                batch.DrawString(_rowFont, name, pos, sel ? new Color(1f, 1f, 1f, 1f) : new Color(0.72f, 0.78f, 0.86f, 1f));
            }

            if (_rooms.Count > 0)
            {
                const string hint = "Up/Down and Enter, or click a room. Esc leaves a room.";
                Vector2 hs = _rowFont.Measure(hint);
                float hy = Top + _rooms.Count * (RowH + 8f) + 24f;
                batch.DrawString(_rowFont, hint, new Vector2((frameW - hs.X) * 0.5f, hy), new Color(0.5f, 0.58f, 0.7f, 1f));
            }
        }
    }
}
