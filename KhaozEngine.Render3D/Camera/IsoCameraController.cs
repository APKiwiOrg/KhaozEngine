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

        /// <summary>
        /// Lower clamp for <see cref="IsoCamera3D.Elevation"/> during an orbit, radians. Default ~15 deg.
        /// Kept &gt; 0 so the view never goes flat or tilts under the board.
        /// </summary>
        public float MinElevation = MathF.PI / 12f;
        /// <summary>
        /// Upper clamp for <see cref="IsoCamera3D.Elevation"/> during an orbit, radians. Default ~88 deg.
        /// Kept strictly &lt; 90 deg so <c>CreateLookAt</c> does not degenerate when the eye direction aligns with up.
        /// </summary>
        public float MaxElevation = MathF.PI * 0.49f;
        /// <summary>Radians of azimuth applied per pixel of horizontal drag during an orbit. Default 0.01.</summary>
        public float OrbitYawSpeed = 0.01f;
        /// <summary>Radians of elevation applied per pixel of vertical drag during an orbit. Default 0.01.</summary>
        public float OrbitPitchSpeed = 0.01f;

        bool _panning;
        Vector3 _grabWorld;   // the fixed world point grabbed at BeginPan, kept under the cursor while panning

        bool _orbiting;
        Vector2 _lastOrbitPx;   // last cursor px seen while orbiting, for the per-frame delta

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

        /// <summary>True between <see cref="BeginOrbit"/> and <see cref="EndOrbit"/>.</summary>
        public bool IsOrbiting => _orbiting;

        /// <summary>Start a cursor-driven orbit: records <paramref name="cursorPx"/> as the drag origin.</summary>
        public void BeginOrbit(Vector2 cursorPx)
        {
            _lastOrbitPx = cursorPx;
            _orbiting = true;
        }

        /// <summary>
        /// Continue an orbit: swings <see cref="IsoCamera3D.Azimuth"/> by the horizontal drag and tilts
        /// <see cref="IsoCamera3D.Elevation"/> by the vertical drag (dragging up raises elevation), clamped to
        /// [<see cref="MinElevation"/>, <see cref="MaxElevation"/>]. <see cref="IsoCamera3D.Target"/> is left fixed,
        /// so the camera swings around the board centre for free. No-op if not currently orbiting.
        /// </summary>
        public void UpdateOrbit(Vector2 cursorPx)
        {
            if (!_orbiting) return;
            Vector2 d = cursorPx - _lastOrbitPx;
            Camera.Azimuth += d.X * OrbitYawSpeed;
            Camera.Elevation = Math.Clamp(Camera.Elevation - d.Y * OrbitPitchSpeed, MinElevation, MaxElevation);
            _lastOrbitPx = cursorPx;
        }

        /// <summary>End the current orbit (if any).</summary>
        public void EndOrbit() => _orbiting = false;

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
