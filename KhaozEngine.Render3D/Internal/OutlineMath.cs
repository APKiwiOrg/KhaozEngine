using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>Camera depth parameters for the edge pass: whether the projection is perspective and its
    /// near/far planes. Extracted from the projection matrix so no camera-interface change is needed.</summary>
    internal readonly struct CameraDepth
    {
        public readonly bool IsPerspective;
        public readonly float Near;
        public readonly float Far;
        public CameraDepth(bool isPerspective, float near, float far)
        {
            IsPerspective = isPerspective; Near = near; Far = far;
        }
    }

    /// <summary>
    /// Pure depth math shared between the C# host (UBO plumbing + tests) and the GLSL <c>EdgeFrag</c>, which
    /// mirrors <see cref="LinearizeDepth"/> exactly (keep in sync, like SurfaceShading.cs mirrors ModelFrag).
    /// System.Numerics <c>CreatePerspectiveFieldOfView</c>/<c>CreatePerspective</c> produce a [0,1] NDC depth
    /// range with M34 == -1; orthographic projections have M34 == 0 (and M44 == 1).
    /// </summary>
    internal static class OutlineMath
    {
        /// <summary>Detect perspective vs orthographic and recover the near/far planes from a System.Numerics
        /// projection matrix. Perspective: M34 != 0, near = M43/M33, far = M43/(M33+1). Orthographic returns
        /// (false, 0, 0) - the edge pass uses the raw linear depth there and never calls
        /// <see cref="LinearizeDepth"/>.</summary>
        public static CameraDepth ExtractCameraDepth(Matrix4x4 p)
        {
            bool perspective = MathF.Abs(p.M34) > 1e-6f;
            if (!perspective) return new CameraDepth(false, 0f, 0f);
            float near = p.M43 / p.M33;
            float far = p.M43 / (p.M33 + 1f);
            return new CameraDepth(true, near, far);
        }

        /// <summary>Convert a stored NDC depth (gl_Position.z/gl_Position.w, [0,1] near-&gt;far) to view-space eye
        /// distance. Inverse of the perspective projection's depth mapping.</summary>
        public static float LinearizeDepth(float ndcDepth, float near, float far)
            => (near * far) / (far - ndcDepth * (far - near));
    }
}
