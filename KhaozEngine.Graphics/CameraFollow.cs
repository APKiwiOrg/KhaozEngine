using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace KhaozEngine.Graphics;

/// <summary>
/// Drives a <see cref="Camera2D"/> to follow a moving target, with frame-rate-independent smoothing,
/// an optional screen-space deadzone, and a world-bounds clamp. The game decides <i>what</i> to follow
/// (which entity, where it is); this owns only the smoothing/deadzone/clamp. Kept separate from the
/// gesture <see cref="CameraController"/> - a screen typically uses one or the other.
///
/// <para>Call <see cref="Update"/> once per frame with the target's world position and the frame's
/// elapsed seconds. The step takes an explicit <see cref="Viewport"/> like <see cref="Camera2D"/>, so
/// it is headless and unit-testable with no <c>GraphicsDevice</c>.</para>
/// </summary>
public sealed class CameraFollow
{
    private readonly Camera2D _camera;

    /// <summary>Creates a follow controller for the given camera.</summary>
    public CameraFollow(Camera2D camera) => _camera = camera;

    /// <summary>The camera this controller drives.</summary>
    public Camera2D Camera => _camera;

    /// <summary>Smoothing rate (per second): higher is snappier. The per-frame catch-up is
    /// <c>1 - exp(-Stiffness * dt)</c>, so the result is independent of frame rate. <c>&lt;= 0</c>
    /// snaps instantly to the target.</summary>
    public float Stiffness { get; set; } = 10f;

    /// <summary>A screen-space (virtual) rectangle the target may move within before the camera chases.
    /// While the target's screen position stays inside it, the camera holds still; once the target
    /// crosses an edge, the camera moves just enough to put it back on that edge. <see cref="Rectangle.Empty"/>
    /// (the default) disables the deadzone, so the camera centers on the target.
    /// <para>Coordinates are absolute virtual screen space - the same space as
    /// <see cref="Camera2D.WorldToScreen(Vector2, Viewport)"/> output. For an inset viewport (non-zero
    /// <c>viewport.X</c>/<c>Y</c>), the rectangle must be given in absolute screen coordinates, not
    /// relative to the viewport's origin.</para></summary>
    public Rectangle Deadzone { get; set; } = Rectangle.Empty;

    /// <summary>
    /// Moves the camera toward <paramref name="target"/> for this frame: computes the desired position
    /// (centered on the target, or held within <see cref="Deadzone"/>), eases toward it by
    /// <see cref="Stiffness"/> over <paramref name="dt"/> seconds (or snaps if <see cref="Stiffness"/>
    /// is non-positive), then clamps so the view stays inside <paramref name="worldBounds"/>.
    /// </summary>
    public void Update(Vector2 target, float dt, Viewport viewport, Rectangle worldBounds)
    {
        Vector2 desired = ComputeDesired(target, viewport);

        if (Stiffness <= 0f || dt <= 0f)
            _camera.Position = desired;
        else
            _camera.Position += (desired - _camera.Position) * (1f - MathF.Exp(-Stiffness * dt));

        _camera.Position = _camera.ClampPosition(_camera.Position, worldBounds, viewport);
    }

    // The position that satisfies the follow rule: center on the target (no deadzone), or shift by the
    // target's screen overflow past the deadzone edges (converted back to world via zoom).
    private Vector2 ComputeDesired(Vector2 target, Viewport viewport)
    {
        if (Deadzone == Rectangle.Empty)
            return target;

        Vector2 screen = _camera.WorldToScreen(target, viewport);
        float dx = screen.X < Deadzone.Left ? screen.X - Deadzone.Left
                 : screen.X > Deadzone.Right ? screen.X - Deadzone.Right : 0f;
        float dy = screen.Y < Deadzone.Top ? screen.Y - Deadzone.Top
                 : screen.Y > Deadzone.Bottom ? screen.Y - Deadzone.Bottom : 0f;

        if ((dx == 0f && dy == 0f) || _camera.Zoom <= 0f)
            return _camera.Position;

        return _camera.Position + new Vector2(dx, dy) / _camera.Zoom;
    }
}
