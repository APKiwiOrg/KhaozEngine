using System.Numerics;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// Single-pointer gesture recognizer: turns a stream of (isDown, position, dt) frames into tap,
    /// long-press, and drag gestures. Feed it once per frame; positions are in whatever space you pass
    /// (use the design-space <see cref="Pointer.Position"/> so gestures match scaled/letterboxed draws).
    /// The per-frame flags (<see cref="Tapped"/>, <see cref="LongPressed"/>, <see cref="DragStarted"/>,
    /// <see cref="DragEnded"/>) are true only on the frame the gesture occurs. Use real (unscaled) dt so a
    /// paused <see cref="GameClock"/> does not stall input timing. Pure / headless-testable.
    /// </summary>
    public sealed class GestureRecognizer
    {
        /// <summary>Movement past this (in fed units) turns a press into a drag and disqualifies a tap.</summary>
        public float MoveThreshold = 8f;
        /// <summary>A press released within this many seconds (and within <see cref="MoveThreshold"/>) is a tap.</summary>
        public float TapMaxDuration = 0.4f;
        /// <summary>Holding still at least this long fires a long-press.</summary>
        public float LongPressDuration = 0.5f;

        bool _down;
        Vector2 _pressPos, _lastPos;
        float _heldSeconds;
        bool _dragging, _longPressFired, _movedPastThreshold;

        // Per-frame results (reset at the top of each Update).
        public bool Tapped { get; private set; }
        public Vector2 TapPosition { get; private set; }
        public bool LongPressed { get; private set; }
        public Vector2 LongPressPosition { get; private set; }
        public bool DragStarted { get; private set; }
        public bool DragEnded { get; private set; }

        /// <summary>True between <see cref="DragStarted"/> and <see cref="DragEnded"/>.</summary>
        public bool IsDragging => _dragging;
        /// <summary>Movement since the previous frame while dragging (zero otherwise).</summary>
        public Vector2 DragDelta { get; private set; }
        /// <summary>Movement since the drag began (zero when not dragging).</summary>
        public Vector2 DragTotal { get; private set; }
        /// <summary>Where the active drag started.</summary>
        public Vector2 DragStart { get; private set; }

        /// <summary>Drive from the design-space pointer. <paramref name="dtSeconds"/> should be real (unscaled) dt.</summary>
        public void Update(Pointer pointer, float dtSeconds) => Update(pointer.IsDown, pointer.Position, dtSeconds);

        /// <summary>Feed one frame of raw pointer state.</summary>
        public void Update(bool isDown, Vector2 position, float dtSeconds)
        {
            Tapped = LongPressed = DragStarted = DragEnded = false;
            DragDelta = Vector2.Zero;

            bool wasDown = _down;
            if (isDown && !wasDown)                       // press begins
            {
                _pressPos = _lastPos = position;
                _heldSeconds = 0f;
                _dragging = _longPressFired = _movedPastThreshold = false;
            }
            else if (isDown)                              // held
            {
                _heldSeconds += dtSeconds;
                float dist = Vector2.Distance(position, _pressPos);
                if (dist > MoveThreshold) _movedPastThreshold = true;

                if (!_dragging && _movedPastThreshold)
                {
                    _dragging = true;
                    DragStarted = true;
                    DragStart = _pressPos;
                    _lastPos = _pressPos;
                }
                if (_dragging)
                {
                    DragDelta = position - _lastPos;
                    DragTotal = position - DragStart;
                    _lastPos = position;
                }
                else if (!_longPressFired && _heldSeconds >= LongPressDuration && !_movedPastThreshold)
                {
                    _longPressFired = true;
                    LongPressed = true;
                    LongPressPosition = position;
                }
            }
            else if (wasDown)                             // release
            {
                if (_dragging) { DragEnded = true; }
                else if (!_longPressFired && !_movedPastThreshold && _heldSeconds <= TapMaxDuration)
                {
                    Tapped = true;
                    TapPosition = position;
                }
                _dragging = false;
                DragTotal = Vector2.Zero;
            }

            _down = isDown;
        }
    }
}
