using System.Numerics;

namespace KhaozEngine.Windowing
{
    /// <summary>Lifecycle of a touch point within a frame.</summary>
    public enum TouchPhase { Began, Moved, Stationary, Ended }

    /// <summary>
    /// One touch point this frame: a stable <see cref="Id"/> (track a finger across frames), its
    /// <see cref="Position"/> (window pixels; map through a <see cref="DesignViewport"/> for design space),
    /// and its <see cref="Phase"/>. Touch is a mobile concern, so on desktop this stays empty; the type and
    /// any gesture mapping over it are still headless-testable.
    /// </summary>
    public readonly struct TouchPoint
    {
        public long Id { get; }
        public Vector2 Position { get; }
        public TouchPhase Phase { get; }

        public TouchPoint(long id, Vector2 position, TouchPhase phase)
        {
            Id = id; Position = position; Phase = phase;
        }
    }
}
