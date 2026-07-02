using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>The hub root scene: renders one row per registered room and pushes the chosen room. Up/Down (or
    /// W/S) move the highlight, Enter/Space or a click on a row enters that room. An empty registry shows nothing
    /// to enter (used only before rooms are registered).</summary>
    public sealed class MenuScene : GameScene
    {
        readonly Texture2D _white;
        readonly IReadOnlyList<(string Name, Func<GameScene> Factory)> _rooms;
        readonly ShowcaseMenu _menu;

        const float RowH = 48f, RowW = 360f, Top = 120f;

        public MenuScene(Texture2D white, IReadOnlyList<(string Name, Func<GameScene> Factory)> rooms)
        {
            _white = white;
            _rooms = rooms;
            var names = new List<string>(rooms.Count);
            foreach (var r in rooms) names.Add(r.Name);
            _menu = new ShowcaseMenu(names);
        }

        static Rect RowRect(int i, float frameW) => new Rect(frameW * 0.5f - RowW * 0.5f, Top + i * (RowH + 8f), RowW, RowH);

        public override void OnUpdate(float dt)
        {
            var m = Manager!;
            if (m.Input.WasPressed(Key.Down) || m.Input.WasPressed(Key.S)) _menu.MoveNext();
            if (m.Input.WasPressed(Key.Up) || m.Input.WasPressed(Key.W)) _menu.MovePrev();

            // Click a row: hit-test each row rect with the pointer press-origin helper.
            if (m.Pointer is { } p && p.IsJustReleased)
                for (int i = 0; i < _rooms.Count; i++)
                    if (p.IsTapIn(RowRect(i, m.FrameWidth))) { _menu.SelectAt(i); Enter(m); return; }

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
            batch.Draw(_white, new Vector4(0, 0, m.FrameWidth, m.FrameHeight), new Color(0.10f, 0.13f, 0.20f, 1f));
            for (int i = 0; i < _rooms.Count; i++)
            {
                bool sel = i == _menu.Selected;
                var r = RowRect(i, m.FrameWidth);
                batch.Draw(_white, new Vector4(r.X, r.Y, r.Width, r.Height),
                    sel ? new Color(0.30f, 0.55f, 0.85f, 1f) : new Color(0.20f, 0.24f, 0.32f, 1f));
            }
        }
    }
}
