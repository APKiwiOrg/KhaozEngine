using System.Numerics;

namespace KhaozEngine.Windowing
{
    /// <summary>An axis-aligned rectangle in pixels (top-left origin). Used for input hit-testing.</summary>
    public readonly record struct Rect(float X, float Y, float Width, float Height)
    {
        public float Right => X + Width;
        public float Bottom => Y + Height;

        /// <summary>True if <paramref name="p"/> is inside (left/top inclusive, right/bottom exclusive).</summary>
        public bool Contains(Vector2 p) => p.X >= X && p.Y >= Y && p.X < X + Width && p.Y < Y + Height;
    }
}
