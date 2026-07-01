using System.Numerics;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// A camera region: a world rectangle that is both the trigger area (the followed target must be inside
    /// for the room to be active) and the camera confinement (in-room follow clamps to it), plus an optional
    /// per-room zoom override. Used by <see cref="RoomCamera"/>.
    /// </summary>
    public readonly struct CameraRoom
    {
        /// <summary>The region rectangle, in world units.</summary>
        public readonly Rect Bounds;

        /// <summary>Optional zoom override applied on entry; <c>null</c> keeps the current zoom.</summary>
        public readonly float? Zoom;

        public CameraRoom(Rect bounds, float? zoom = null)
        {
            Bounds = bounds;
            Zoom = zoom;
        }

        /// <summary>True when <paramref name="worldPoint"/> is inside <see cref="Bounds"/>.</summary>
        public bool Contains(Vector2 worldPoint) => Bounds.Contains(worldPoint);
    }
}
