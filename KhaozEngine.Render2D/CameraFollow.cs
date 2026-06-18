using System;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Drives a <see cref="Camera2D"/> to follow a moving target, with per-axis frame-rate-independent
    /// smoothing, an optional screen-space deadzone, a world-bounds clamp, and an instant <see cref="Warp"/>.
    /// The game decides <i>what</i> to follow; this owns only the feel. Pure <see cref="System.Numerics"/>,
    /// headless, no GPU.
    ///
    /// <para>Smoothing runs on an internal sub-pixel position so a later pixel-snap can round only the
    /// rendered <see cref="Camera2D.Position"/> without drift. Call <see cref="Update(Vector2, Vector2, float,
    /// int, int, Rect)"/> once per frame.</para>
    /// </summary>
    public sealed class CameraFollow
    {
        private readonly Camera2D _camera;
        private Vector2 _smoothPos;     // sub-pixel-accurate truth the smoothing operates on
        private Vector2 _leadOffset;    // currently-applied (eased) look-ahead offset
        private bool _initialized;      // false until the first Update / Warp seeds _smoothPos

        /// <summary>Creates a follow controller for the given camera.</summary>
        public CameraFollow(Camera2D camera) => _camera = camera;

        /// <summary>The camera this controller drives.</summary>
        public Camera2D Camera => _camera;

        /// <summary>Per-axis smoothing rate (per second): higher is snappier. The per-frame catch-up on each
        /// axis is <c>1 - exp(-Stiffness.axis * dt)</c>, independent of frame rate. A component &lt;= 0 snaps
        /// that axis instantly.</summary>
        public Vector2 Stiffness { get; set; } = new(10f, 10f);

        /// <summary>Convenience: sets both axes of <see cref="Stiffness"/> to the same value.</summary>
        public void SetStiffness(float both) => Stiffness = new Vector2(both, both);

        /// <summary>Look-ahead configuration. <c>default</c> (zero lead time) disables it.</summary>
        public LookAheadSettings LookAhead { get; set; }

        /// <summary>An absolute screen-space rectangle the target may move within before the camera chases
        /// (same space as <see cref="Camera2D.WorldToScreen(Vector2, int, int)"/> output, rotation assumed 0).
        /// While the target's screen position stays inside it the camera holds; crossing an edge moves the
        /// camera just enough to put the target back on that edge. <c>null</c> (default) centers on the
        /// target.</summary>
        public Rect? Deadzone { get; set; }

        /// <summary>
        /// Follow step. Eases the camera toward <paramref name="target"/> (held within <see cref="Deadzone"/>
        /// if set), then clamps so the view stays inside <paramref name="worldBounds"/>.
        /// <paramref name="velocity"/> drives the look-ahead offset when <see cref="LookAhead"/> is configured.
        /// </summary>
        public void Update(Vector2 target, Vector2 velocity, float dt,
                           int viewportWidth, int viewportHeight, Rect worldBounds)
        {
            if (!_initialized) { _smoothPos = _camera.Position; _initialized = true; }

            Vector2 desired = ComputeDesired(target, viewportWidth, viewportHeight);
            desired += UpdateLeadOffset(velocity, dt);

            _smoothPos = new Vector2(
                EaseAxis(_smoothPos.X, desired.X, Stiffness.X, dt),
                EaseAxis(_smoothPos.Y, desired.Y, Stiffness.Y, dt));

            _smoothPos = _camera.ClampPosition(_smoothPos, worldBounds, viewportWidth, viewportHeight);

            _camera.Position = _smoothPos;
        }

        /// <summary>Convenience overload with zero velocity.</summary>
        public void Update(Vector2 target, float dt, int viewportWidth, int viewportHeight, Rect worldBounds)
            => Update(target, Vector2.Zero, dt, viewportWidth, viewportHeight, worldBounds);

        /// <summary>Hard-sets the camera to <paramref name="position"/>, bypassing smoothing. For respawn /
        /// scene load so the camera does not ease across the level.</summary>
        public void Warp(Vector2 position)
        {
            _smoothPos = position;
            _leadOffset = Vector2.Zero;
            _initialized = true;
            _camera.Position = position;
        }

        private static float EaseAxis(float current, float desired, float stiffness, float dt)
            => stiffness <= 0f || dt <= 0f
                ? desired
                : current + (desired - current) * (1f - MathF.Exp(-stiffness * dt));

        // The position that satisfies the follow rule: center on the target (no deadzone), or shift by the
        // target's screen overflow past the deadzone edges (converted back to world via zoom). Rotation 0.
        private Vector2 ComputeDesired(Vector2 target, int vw, int vh)
        {
            if (Deadzone is not Rect dz) return target;

            // Screen position of the target relative to the current sub-pixel camera position (rotation 0).
            float zoom = _camera.Zoom;
            var screen = new Vector2(
                (target.X - _smoothPos.X) * zoom + vw * 0.5f,
                (target.Y - _smoothPos.Y) * zoom + vh * 0.5f);

            float dx = screen.X < dz.X ? screen.X - dz.X
                     : screen.X > dz.Right ? screen.X - dz.Right : 0f;
            float dy = screen.Y < dz.Y ? screen.Y - dz.Y
                     : screen.Y > dz.Bottom ? screen.Y - dz.Bottom : 0f;

            if ((dx == 0f && dy == 0f) || zoom <= 0f) return _smoothPos;

            return _smoothPos + new Vector2(dx, dy) / zoom;
        }

        // Eases _leadOffset toward clamp(velocity * LeadTime, +/-MaxDistance) per axis, returns the new offset.
        private Vector2 UpdateLeadOffset(Vector2 velocity, float dt)
        {
            var leadTarget = new Vector2(
                ClampAxis(velocity.X * LookAhead.LeadTime.X, LookAhead.MaxDistance.X),
                ClampAxis(velocity.Y * LookAhead.LeadTime.Y, LookAhead.MaxDistance.Y));

            _leadOffset = new Vector2(
                EaseAxis(_leadOffset.X, leadTarget.X, LookAhead.Stiffness, dt),
                EaseAxis(_leadOffset.Y, leadTarget.Y, LookAhead.Stiffness, dt));

            return _leadOffset;
        }

        // Clamps value to [-max, max]; max <= 0 means unclamped.
        private static float ClampAxis(float value, float max)
            => max <= 0f ? value : System.Math.Clamp(value, -max, max);
    }
}
