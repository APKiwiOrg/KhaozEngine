using System.Numerics;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Immutable snapshot of a <see cref="Camera2D"/>'s framing: where it looks (<see cref="Position"/>),
    /// how far in (<see cref="Zoom"/>), and its roll (<see cref="Rotation"/>). Used as the endpoint of a
    /// <see cref="CameraBlend"/> and as a reusable camera "setup" value.
    /// </summary>
    public readonly struct CameraState
    {
        public readonly Vector2 Position;
        public readonly float   Zoom;
        public readonly float   Rotation;

        public CameraState(Vector2 position, float zoom, float rotation)
        {
            Position = position;
            Zoom = zoom;
            Rotation = rotation;
        }

        /// <summary>Snapshots the camera's current Position/Zoom/Rotation.</summary>
        public static CameraState From(Camera2D camera) => new(camera.Position, camera.Zoom, camera.Rotation);

        /// <summary>Writes this state onto <paramref name="camera"/>.</summary>
        public void ApplyTo(Camera2D camera)
        {
            camera.Position = Position;
            camera.Zoom = Zoom;
            camera.Rotation = Rotation;
        }

        /// <summary>Per-field linear interpolation (Position via <see cref="Vector2.Lerp(System.Numerics.Vector2, System.Numerics.Vector2, float)"/>; Zoom/Rotation
        /// scalar). Rotation is interpolated linearly - no shortest-arc wrap; callers supply sane angles.</summary>
        public static CameraState Lerp(CameraState a, CameraState b, float t) => new(
            Vector2.Lerp(a.Position, b.Position, t),
            a.Zoom + (b.Zoom - a.Zoom) * t,
            a.Rotation + (b.Rotation - a.Rotation) * t);
    }
}
