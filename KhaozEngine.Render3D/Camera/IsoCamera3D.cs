using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Orthographic (no perspective) isometric camera. Defaults reproduce a 2:1 iso look
    /// (azimuth 45 deg, elevation atan(0.5) ~= 26.57 deg). Angle, ortho size/zoom, and target are all
    /// configurable so true-iso 30-35 deg can be tried by eye. Pure System.Numerics, no GPU.
    ///
    /// Convention: Y up, right-handed. Eye = Target + Distance * dirToEye, where
    /// dirToEye = normalize(cosE*sinA, sinE, cosE*cosA). Forward = -dirToEye.
    /// </summary>
    public sealed class IsoCamera3D : IIsoCamera3D, IRenderOriginAware
    {
        /// <summary>Rotation about the Y (up) axis, radians. Default 45 deg.</summary>
        public float Azimuth = MathF.PI / 4f;
        /// <summary>Tilt above the horizontal, radians. Default atan(0.5) ~= 26.57 deg (2:1 iso).</summary>
        public float Elevation = MathF.Atan(0.5f);
        /// <summary>World-space point the camera looks at.</summary>
        public Vector3 Target = Vector3.Zero;
        /// <summary>Vertical world extent covered by the viewport (before zoom). Larger = more zoomed out.</summary>
        public float OrthoSize = 10f;
        /// <summary>Zoom multiplier (2 = twice as large on screen).</summary>
        public float Zoom = 1f;
        /// <summary>Viewport aspect (width/height).</summary>
        public float AspectRatio = 16f / 9f;
        /// <summary>Distance of the eye from the target (ortho: affects clipping, not size).</summary>
        public float Distance = 50f;
        public float NearPlane = 0.1f;
        public float FarPlane = 200f;

        Vector3 DirToEye
        {
            get
            {
                float cE = MathF.Cos(Elevation), sE = MathF.Sin(Elevation);
                float cA = MathF.Cos(Azimuth), sA = MathF.Sin(Azimuth);
                return Vector3.Normalize(new Vector3(cE * sA, sE, cE * cA));
            }
        }

        public Vector3 Eye => Target + DirToEye * Distance;
        public Vector3 Forward => -DirToEye;

        /// <summary>The render origin eye and target are expressed against when building <see cref="View"/>. See
        /// <see cref="IRenderOriginAware"/>. <see cref="Vector3.Zero"/> (the default) is the pre-floating-origin
        /// camera, bit for bit.</summary>
        public Vector3 RenderOrigin { get; set; }

        public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye - RenderOrigin, Target - RenderOrigin, Vector3.UnitY);

        /// <summary>The view built from the ABSOLUTE eye and target, ignoring <see cref="RenderOrigin"/>. Backs
        /// <see cref="AbsoluteViewProjection"/> and the <see cref="Frame"/> fit, both of which work in absolute
        /// world space.</summary>
        Matrix4x4 AbsoluteView => Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY);

        /// <summary>The pre-shift view-projection. See <see cref="IRenderOriginAware.AbsoluteViewProjection"/>.</summary>
        public Matrix4x4 AbsoluteViewProjection => AbsoluteView * Projection;

        public Matrix4x4 Projection
        {
            get
            {
                float h = OrthoSize / Zoom;
                float w = h * AspectRatio;
                return Matrix4x4.CreateOrthographic(w, h, NearPlane, FarPlane);
            }
        }

        public Matrix4x4 ViewProjection => View * Projection;

        /// <summary>
        /// Aim the camera at <paramref name="center"/> and size <see cref="OrthoSize"/> so an axis-aligned
        /// bounds of full extent <paramref name="size"/> fits the viewport (with a <paramref name="margin"/>
        /// of slack, e.g. 1.1 = 10%). Projects the 8 corners into view space and fits both axes against the
        /// current <see cref="AspectRatio"/>/<see cref="Zoom"/>. Pure math.
        /// </summary>
        public void Frame(Vector3 center, Vector3 size, float margin = 1.1f)
        {
            Target = center;
            Matrix4x4 view = AbsoluteView;   // the corners below are ABSOLUTE world points, so fit against the absolute view
            Vector3 h = size * 0.5f;
            float maxX = 0f, maxY = 0f;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        var v = Vector3.Transform(center + new Vector3(sx * h.X, sy * h.Y, sz * h.Z), view);
                        maxX = MathF.Max(maxX, MathF.Abs(v.X));
                        maxY = MathF.Max(maxY, MathF.Abs(v.Y));
                    }
            // OrthoSize is the full vertical world extent; the viewport is OrthoSize/Zoom tall and that*Aspect
            // wide. Cover both: OrthoSize >= 2*Zoom*maxY and >= 2*Zoom*maxX/Aspect.
            float needed = MathF.Max(2f * maxY, 2f * maxX / AspectRatio);
            OrthoSize = needed * Zoom * margin;
        }

        /// <summary>Project a world point to a screen pixel (forward inverse of <see cref="ScreenToRay"/>); false
        /// when the point is not in front of the camera. See <see cref="IIsoCamera3D.WorldToScreen(Vector3, int, int, out Vector2)"/>.</summary>
        public bool WorldToScreen(Vector3 world, int viewportWidth, int viewportHeight, out Vector2 screenPixel) =>
            CameraProjection.WorldToScreen(ViewProjection, world - RenderOrigin, viewportWidth, viewportHeight, out screenPixel);

        /// <summary>
        /// Unproject a screen pixel (top-left origin, y-down) into a world ray. For this orthographic camera
        /// the direction equals <see cref="Forward"/>; the math is general so it still holds if a perspective
        /// camera is added. Inverts <see cref="ViewProjection"/>, which matches the displayed image.
        /// </summary>
        public Ray ScreenToRay(Vector2 screenPixel, int viewportWidth, int viewportHeight)
        {
            float ndcX = screenPixel.X / viewportWidth * 2f - 1f;
            float ndcY = 1f - screenPixel.Y / viewportHeight * 2f;
            Matrix4x4.Invert(ViewProjection, out var inv);
            Vector3 near = Unproject(new Vector3(ndcX, ndcY, 0f), inv);
            Vector3 far = Unproject(new Vector3(ndcX, ndcY, 1f), inv);
            // The unprojection lands in the RENDER frame, so add the origin back: the ray this returns is absolute
            // world, as it always was. The direction is a difference and is frame-invariant.
            return new Ray(near + RenderOrigin, far - near);
        }

        /// <summary>Pick the world point under a screen pixel on the horizontal plane y = <paramref name="groundY"/>.</summary>
        public Vector3 ScreenToGround(Vector2 screenPixel, int viewportWidth, int viewportHeight, float groundY = 0f)
        {
            Ray r = ScreenToRay(screenPixel, viewportWidth, viewportHeight);
            float t = MathF.Abs(r.Direction.Y) < 1e-6f ? 0f : (groundY - r.Origin.Y) / r.Direction.Y;
            return r.Origin + r.Direction * t;
        }

        static Vector3 Unproject(Vector3 ndc, Matrix4x4 invViewProj)
        {
            var p = Vector4.Transform(new Vector4(ndc, 1f), invViewProj);
            return new Vector3(p.X, p.Y, p.Z) / p.W;
        }
    }
}
