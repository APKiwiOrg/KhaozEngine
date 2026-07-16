using System.Collections.Generic;

namespace KhaozEngine.Showcase
{
    /// <summary>GPU-free menu navigation model: the room-name list plus a selected index and the grid column
    /// count the tile menu is arranged in. The <see cref="MenuScene"/> is its view. This holds the logic so it
    /// stays headless-testable. <see cref="MoveNext"/>/<see cref="MovePrev"/> wrap through the flat order (the
    /// legacy linear feel). The grid moves (<see cref="MoveLeft"/>/<see cref="MoveRight"/>/<see cref="MoveUp"/>/
    /// <see cref="MoveDown"/>) clamp against the grid edges so arrow keys read as spatial motion over the tiles,
    /// never wrapping across a row or off the odd short last row.</summary>
    public sealed class ShowcaseMenu
    {
        readonly List<string> _rooms;
        readonly int _columns;

        public ShowcaseMenu(IReadOnlyList<string> roomNames, int columns = 1)
        {
            _rooms = new List<string>(roomNames);
            _columns = columns < 1 ? 1 : columns;
            Selected = _rooms.Count > 0 ? 0 : -1;
        }

        public IReadOnlyList<string> Rooms => _rooms;
        public int Selected { get; private set; }
        public string? Current => Selected >= 0 && Selected < _rooms.Count ? _rooms[Selected] : null;

        public void MoveNext() { if (_rooms.Count > 0) Selected = (Selected + 1) % _rooms.Count; }
        public void MovePrev() { if (_rooms.Count > 0) Selected = (Selected - 1 + _rooms.Count) % _rooms.Count; }
        public void SelectAt(int index) { if (index >= 0 && index < _rooms.Count) Selected = index; }

        // Grid moves clamp at the edges (never wrap). Column = Selected % columns, row = Selected / columns.
        public void MoveLeft()
        {
            if (_rooms.Count == 0) return;
            if (Selected % _columns > 0) Selected -= 1;
        }

        public void MoveRight()
        {
            if (_rooms.Count == 0) return;
            if (Selected % _columns < _columns - 1 && Selected + 1 < _rooms.Count) Selected += 1;
        }

        public void MoveUp()
        {
            if (_rooms.Count == 0) return;
            if (Selected - _columns >= 0) Selected -= _columns;
        }

        // Down moves a full row where one exists. From the second-to-last row into a short last row it clamps onto
        // the last populated tile rather than overshooting into an empty cell.
        public void MoveDown()
        {
            if (_rooms.Count == 0) return;
            if (Selected + _columns < _rooms.Count) Selected += _columns;
            else if (Selected / _columns < (_rooms.Count - 1) / _columns) Selected = _rooms.Count - 1;
        }
    }
}
