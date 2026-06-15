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
    public sealed class IsoCamera3D : IIsoCamera3D
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

        public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY);

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
    }
}
