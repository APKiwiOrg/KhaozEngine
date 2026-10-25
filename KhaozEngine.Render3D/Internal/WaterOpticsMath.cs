using System;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>GPU-free reference math for the optical bed depth of one water body.</summary>
    internal static class WaterOpticsMath
    {
        /// <summary>
        /// Converts a signed rest depth to a nonnegative depth at the displaced water surface.
        /// Inputs are already finite. The source reference level stays fixed when the water moves.
        /// </summary>
        public static float BedDepth(float restDepth, float referenceSurfaceY, float displacedSurfaceY)
            => MathF.Max(0f, restDepth + displacedSurfaceY - referenceSurfaceY);
    }
}
