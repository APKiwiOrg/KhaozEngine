using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Input-agnostic zoom + pan controller for an <see cref="IsoCamera3D"/>. It manipulates only the camera
    /// (pure <c>System.Numerics</c>, no GPU and no input types), so the game wires its own input policy to it
    /// (which mouse button pans, scroll source, etc.) and this stays headless-testable.
    ///
    /// Both gestures are CURSOR-ANCHORED on the ground plane: <see cref="Zoom"/> keeps the world point under the
    /// cursor fixed while scaling, and the grab-pan (<see cref="BeginPan"/>/<see cref="UpdatePan"/>) keeps the
    /// world point grabbed at the start of the drag under the cursor, so the ground appears to move with the hand.
    /// </summary>
    public sealed class IsoCameraController
    {
        /// <summary>The camera this controller drives.</summary>
        public IsoCamera3D Camera { get; }

        /// <summary>Minimum <see cref="IsoCamera3D.Zoom"/> (most zoomed out). Default 0.4.</summary>
        public float MinZoom = 0.4f;
        /// <summary>Maximum <see cref="IsoCamera3D.Zoom"/> (most zoomed in). Default 4.0.</summary>
        public float MaxZoom = 4.0f;
        /// <summary>Multiplicative zoom factor applied per unit of wheel delta. Default 1.1.</summary>
        public float ZoomStep = 1.1f;
        /// <summary>The ground-plane height the gestures pick against. Default 0.</summary>
        public float GroundY = 0f;

        /// <summary>Optional inclusive lower bound for <see cref="IsoCamera3D.Target"/> X/Z (Y is left untouched).</summary>
        public Vector3? PanMin;
        /// <summary>Optional inclusive upper bound for <see cref="IsoCamera3D.Target"/> X/Z (Y is left untouched).</summary>
        public Vector3? PanMax;

        bool _panning;
        Vector3 _grabWorld;   // the fixed world point grabbed at BeginPan, kept under the cursor while panning

        public IsoCameraController(IsoCamera3D camera)
        {
            Camera = camera ?? throw new ArgumentNullException(nameof(camera));
        }

        /// <summary>True between <see cref="BeginPan"/> and <see cref="EndPan"/>.</summary>
        public bool IsPanning => _panning;

        /// <summary>
        /// Scroll-zoom anchored at the cursor: scales <see cref="IsoCamera3D.Zoom"/> by
        /// <see cref="ZoomStep"/>^<paramref name="wheelDelta"/> (clamped to [<see cref="MinZoom"/>,
        /// <see cref="MaxZoom"/>]) and shifts <see cref="IsoCamera3D.Target"/> so the ground point under
        /// <paramref name="cursorPx"/> stays put. Positive <paramref name="wheelDelta"/> zooms in. A delta of 0
        /// (or a no-op clamp) leaves the camera unchanged.
        /// </summary>
        public void Zoom(float wheelDelta, Vector2 cursorPx, int viewportWidth, int viewportHeight)
        {
            if (wheelDelta == 0f) return;
            float target = Math.Clamp(Camera.Zoom * MathF.Pow(ZoomStep, wheelDelta), MinZoom, MaxZoom);
            if (target == Camera.Zoom) return;

            Vector3 anchor = Camera.ScreenToGround(cursorPx, viewportWidth, viewportHeight, GroundY);
            Camera.Zoom = target;
            Vector3 after = Camera.ScreenToGround(cursorPx, viewportWidth, viewportHeight, GroundY);
            Camera.Target += anchor - after;   // re-pin the anchor under the cursor
            ClampTarget();
        }

        /// <summary>Start a grab-pan: records the world point under <paramref name="cursorPx"/> as the grab anchor.</summary>
        public void BeginPan(Vector2 cursorPx, int viewportWidth, int viewportHeight)
        {
            _grabWorld = Camera.ScreenToGround(cursorPx, viewportWidth, viewportHeight, GroundY);
            _panning = true;
        }

        /// <summary>
        /// Continue a grab-pan: shifts <see cref="IsoCamera3D.Target"/> so the grab anchor sits under
        /// <paramref name="cursorPx"/> again (the ground follows the hand). No-op if not currently panning.
        /// </summary>
        public void UpdatePan(Vector2 cursorPx, int viewportWidth, int viewportHeight)
        {
            if (!_panning) return;
            Vector3 under = Camera.ScreenToGround(cursorPx, viewportWidth, viewportHeight, GroundY);
            Camera.Target += _grabWorld - under;
            ClampTarget();
        }

        /// <summary>End the current grab-pan (if any).</summary>
        public void EndPan() => _panning = false;

        void ClampTarget()
        {
            if (PanMin is Vector3 mn && PanMax is Vector3 mx)
                Camera.Target = new Vector3(
                    Math.Clamp(Camera.Target.X, mn.X, mx.X),
                    Camera.Target.Y,
                    Math.Clamp(Camera.Target.Z, mn.Z, mx.Z));
        }
    }
}
