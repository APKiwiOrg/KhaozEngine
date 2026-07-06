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
    /// <summary>The hub root scene: a title, one <see cref="Button"/> per registered room, and a hint line, all drawn
    /// through the engine's own Gui so the landing screen wears the same crisp <see cref="GuiTheme"/> as the widget
    /// room. Up/Down (or W/S) move the highlight; the highlighted row draws in the accent <see cref="GuiStyle.Active"/>
    /// preset and the rest in the muted <see cref="GuiStyle.Secondary"/>. Enter/Space or a click on a row enters that
    /// room. An empty registry shows nothing to enter (used only before rooms are registered).</summary>
    // The title, hint, and room-name captions are developer-facing hub chrome (room names are defined as raw literals
    // in ShowcaseApp's registry), not localizable player copy, so the raw captions here are intentional - the same
    // KELOC escape hatch the [LocalizationExempt] room screens use.
    [LocalizationExempt]
    public sealed class MenuScene : GameScene
    {
        readonly Texture2D _white;
        readonly SpriteFont _titleFont;
        readonly SpriteFont _rowFont;
        readonly IReadOnlyList<(string Name, Func<GameScene> Factory)> _rooms;
        readonly ShowcaseMenu _menu;
        readonly List<Button> _rows = new();

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

            // One retained Button per room, wearing the crisp theme. A click selects that row and enters it (the same
            // path as Enter on the keyboard); bounds are (re)laid out from the design width each frame in Layout.
            for (int i = 0; i < _rooms.Count; i++)
            {
                int index = i;   // capture per-iteration for the click handler
                _rows.Add(new Button(default, LocalizedText.Raw(_rooms[i].Name), _rowFont, () => Choose(index)));
            }
        }

        // Rows are laid out and hit-tested in DESIGN space (the width the SpriteBatch draws in and the Pointer
        // maps into), never window/frame pixels - otherwise a resized window pushes the menu off-centre because
        // the design-space batch and pointer disagree with a frame-pixel layout.
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

            // Lay the row buttons out for the current design width, then drive them off the pointer: each button's
            // click handler enters its room (Choose), so a tap on a row does the same as Enter on that selection.
            Layout(m.Viewport!.DesignBounds.Width);
            if (m.Pointer is { } p)
                foreach (var row in _rows)
                    if (row.Update(p)) return;   // a row was clicked and entered its room; nothing more this frame

            if (m.Input.WasPressed(Key.Enter) || m.Input.WasPressed(Key.Space)) Enter(m);
        }

        // A row's click handler: select it (so the highlight follows the mouse) and enter it.
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

        public override void OnDraw2D(SpriteBatch batch)
        {
            var m = Manager!;
            // Everything below is drawn in design space (SpriteBatch.Begin(viewport) is already active), so centre
            // on the design bounds, not FrameWidth/FrameHeight - those are window pixels and drift on resize.
            Rect db = m.Viewport!.DesignBounds;
            float frameW = db.Width;
            GuiTheme theme = GuiTheme.Default;
            Layout(frameW);

            batch.Draw(_white, new Vector4(0, 0, frameW, db.Height), (Color)theme.Background);

            const string title = "KhaozEngine Showcase";
            Vector2 ts = _titleFont.Measure(title);
            batch.DrawString(_titleFont, title, new Vector2((frameW - ts.X) * 0.5f, 60f), (Color)theme.Text);

            for (int i = 0; i < _rows.Count; i++)
            {
                // The highlighted row wears the accent Active preset; the rest the muted Secondary. Selection is
                // expressed through the resting style (not Button.Selected) so the whole Active palette reads.
                _rows[i].Style = i == _menu.Selected ? GuiStyle.Active : GuiStyle.Secondary;
                _rows[i].Draw(batch, _white);
            }

            if (_rooms.Count > 0)
            {
                const string hint = "Up/Down and Enter, or click a room. Esc leaves a room.";
                Vector2 hs = _rowFont.Measure(hint);
                float hy = Top + _rooms.Count * (RowH + 8f) + 24f;
                batch.DrawString(_rowFont, hint, new Vector2((frameW - hs.X) * 0.5f, hy), (Color)theme.TextMuted);
            }
        }
    }
}
