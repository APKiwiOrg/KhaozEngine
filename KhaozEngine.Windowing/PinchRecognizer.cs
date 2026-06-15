using System.Numerics;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// Two-point pinch recognizer: turns a stream of (active, pointA, pointB) frames into a relative
    /// <see cref="Scale"/> (vs the gesture start), a per-frame <see cref="ScaleDelta"/>, and a
    /// <see cref="PanDelta"/> of the midpoint. Feed it once per frame with the two active touch points
    /// (mobile); on desktop it is exercised by tests. Pure / headless. Pass points in design space to drive a
    /// <c>Camera2D</c> zoom/pan consistently with scaled draws.
    /// </summary>
    public sealed class PinchRecognizer
    {
        bool _active;
        float _startDistance, _prevDistance;
        Vector2 _prevCenter;

        /// <summary>True while two points are active.</summary>
        public bool IsPinching => _active;
        /// <summary>Current distance / distance at gesture start (1 at start, &gt;1 spreading, &lt;1 closing).</summary>
        public float Scale { get; private set; } = 1f;
        /// <summary>Current distance / previous frame's distance (1 when not pinching or on the start frame).</summary>
        public float ScaleDelta { get; private set; } = 1f;
        /// <summary>Midpoint movement since the previous frame (zero when not pinching).</summary>
        public Vector2 PanDelta { get; private set; }
        /// <summary>Current midpoint of the two points.</summary>
        public Vector2 Center { get; private set; }

        /// <summary>Feed one frame. <paramref name="active"/> is true only when both points are down.</summary>
        public void Update(bool active, Vector2 a, Vector2 b)
        {
            ScaleDelta = 1f;
            PanDelta = Vector2.Zero;

            float distance = Vector2.Distance(a, b);
            Vector2 center = (a + b) * 0.5f;

            if (active && !_active)                       // pinch begins
            {
                _startDistance = _prevDistance = distance > 0f ? distance : 1f;
                _prevCenter = center;
                Scale = 1f;
                Center = center;
            }
            else if (active)                              // continuing
            {
                if (_prevDistance > 0f) ScaleDelta = distance / _prevDistance;
                if (_startDistance > 0f) Scale = distance / _startDistance;
                PanDelta = center - _prevCenter;
                Center = center;
                _prevDistance = distance;
                _prevCenter = center;
            }
            else                                          // not pinching
            {
                Scale = 1f;
            }

            _active = active;
        }
    }
}
