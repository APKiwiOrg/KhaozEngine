using System.Collections.Generic;

namespace KhaozEngine.Showcase
{
    /// <summary>GPU-free menu navigation model: the room-name list plus a wrapping selected index.
    /// The <see cref="MenuScene"/> is its view. This holds the logic so it stays headless-testable.</summary>
    public sealed class ShowcaseMenu
    {
        readonly List<string> _rooms;
        public ShowcaseMenu(IReadOnlyList<string> roomNames)
        {
            _rooms = new List<string>(roomNames);
            Selected = _rooms.Count > 0 ? 0 : -1;
        }

        public IReadOnlyList<string> Rooms => _rooms;
        public int Selected { get; private set; }
        public string? Current => Selected >= 0 && Selected < _rooms.Count ? _rooms[Selected] : null;

        public void MoveNext() { if (_rooms.Count > 0) Selected = (Selected + 1) % _rooms.Count; }
        public void MovePrev() { if (_rooms.Count > 0) Selected = (Selected - 1 + _rooms.Count) % _rooms.Count; }
        public void SelectAt(int index) { if (index >= 0 && index < _rooms.Count) Selected = index; }
    }
}
