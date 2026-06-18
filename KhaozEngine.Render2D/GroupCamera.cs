using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Drives a <see cref="Camera2D"/> to frame multiple targets (co-op / shared screen): each frame it
    /// computes the targets' padded bounding box via <see cref="CameraFraming"/> and eases the camera's
    /// position and zoom toward the framing (frame-rate-independent), then clamps to world bounds. The game
    /// supplies the target positions; this owns the framing + smoothing. Headless, no GPU.
    /// </summary>
    public sealed class GroupCamera
    {
        private readonly Camera2D _camera;

        /// <summary>Creates a group camera driving the given camera.</summary>
        public GroupCamera(Camera2D camera) => _camera = camera;

        /// <summary>The camera this controller drives.</summary>
        public Camera2D Camera => _camera;

        /// <summary>Position smoothing rate (per second): <c>1 - exp(-Stiffness*dt)</c> per frame. <c>&lt;= 0</c>
        /// snaps position instantly.</summary>
        public float Stiffness { get; set; } = 8f;

        /// <summary>Zoom smoothing rate (per second), separate from <see cref="Stiffness"/> so zoom can lag or
        /// lead position. <c>&lt;= 0</c> snaps zoom instantly.</summary>
        public float ZoomStiffness { get; set; } = 8f;

        /// <summary>Margin around the targets, as a fraction of their extent added on each side.</summary>
        public float PaddingFraction { get; set; } = 0.15f;

        /// <summary>Floor on the framed box extent (world units, per axis): keeps zoom sane when targets cluster
        /// (a single target frames a box of this size rather than fitting a zero-area point).</summary>
        public Vector2 MinViewSize { get; set; } = new(1f, 1f);

        /// <summary>Lower zoom clamp.</summary>
        public float MinZoom { get; set; } = 0.0001f;

        /// <summary>Upper zoom clamp.</summary>
        public float MaxZoom { get; set; } = float.MaxValue;

        /// <summary>Eases the camera toward the framing of <paramref name="targets"/>, then clamps position to
        /// <paramref name="worldBounds"/>. Empty <paramref name="targets"/> holds the current view.</summary>
        public void Update(IReadOnlyList<Vector2> targets, float dt, int viewportWidth, int viewportHeight, Rect worldBounds)
        {
            if (targets == null || targets.Count == 0) return;

            var (desiredPos, desiredZoom) = SolveFor(targets, viewportWidth, viewportHeight);

            _camera.Zoom = Ease(_camera.Zoom, desiredZoom, ZoomStiffness, dt);
            _camera.Position = new Vector2(
                Ease(_camera.Position.X, desiredPos.X, Stiffness, dt),
                Ease(_camera.Position.Y, desiredPos.Y, Stiffness, dt));

            _camera.Position = _camera.ClampPosition(_camera.Position, worldBounds, viewportWidth, viewportHeight);
        }

        /// <summary>Snaps the camera directly to the framing of <paramref name="targets"/> (no easing), then
        /// clamps position to <paramref name="worldBounds"/>. Empty <paramref name="targets"/> is a no-op.</summary>
        public void Warp(IReadOnlyList<Vector2> targets, int viewportWidth, int viewportHeight, Rect worldBounds)
        {
            if (targets == null || targets.Count == 0) return;

            var (desiredPos, desiredZoom) = SolveFor(targets, viewportWidth, viewportHeight);
            _camera.Zoom = desiredZoom;
            _camera.Position = desiredPos;
            _camera.Position = _camera.ClampPosition(_camera.Position, worldBounds, viewportWidth, viewportHeight);
        }

        private (Vector2 Position, float Zoom) SolveFor(IReadOnlyList<Vector2> targets, int vw, int vh)
        {
            var bounds = CameraFraming.Bounds(targets, PaddingFraction, MinViewSize);
            return CameraFraming.Solve(bounds, vw, vh, MinZoom, MaxZoom);
        }

        private static float Ease(float current, float desired, float rate, float dt)
            => rate <= 0f || dt <= 0f ? desired : current + (desired - current) * (1f - MathF.Exp(-rate * dt));
    }
}
