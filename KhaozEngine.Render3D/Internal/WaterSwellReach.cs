using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// How far the procedural Gerstner swell can move a still-water point, and whether it moves one at all. Pure, so
    /// the water renderer's flat-quad routing is headless-testable against <see cref="GerstnerWaves"/>, the CPU
    /// mirror of the swell both water stages evaluate.
    /// </summary>
    internal static class WaterSwellReach
    {
        /// <summary>
        /// Whether a swell displaces the surface at all. The exact complement of the gate at the top of
        /// <c>gerstnerEvaluate</c> (ShaderSources.WaterSwell.cs), which returns a zero offset unless the amplitude
        /// AND the wavelength are positive, and of the same gate in the fragment's swell attenuation. So a zero or
        /// negative amplitude and a zero or negative wavelength all leave the surface flat, which a test on a zero
        /// amplitude alone misses.
        /// </summary>
        public static bool Displaces(float amplitude, float wavelength) => amplitude > 0f && wavelength > 0f;

        const float InverseTwoPi = 0.15915494f;

        /// <summary>
        /// The largest offset the swell can give any point, as (horizontal, vertical) metres, and zero when it does
        /// not displace. Vertical: the component amplitudes are normalized to sum to <paramref name="amplitude"/>.
        /// Horizontal: each component's orbital radius is <c>steepness / (k_i * n)</c>, which is
        /// <c>|steepness| * lambda_i / (2 pi n)</c>, and no component is longer than <paramref name="wavelength"/>,
        /// so the n radii sum to at most <c>|steepness| * wavelength / (2 pi)</c>. That reach does not shrink with
        /// the amplitude, which is why a cull grown by the amplitude alone would drop crests the pinch carries into view.
        /// </summary>
        public static Vector2 Of(float amplitude, float wavelength, float steepness)
            => Displaces(amplitude, wavelength)
                ? new Vector2(MathF.Abs(steepness) * wavelength * InverseTwoPi, amplitude)
                : Vector2.Zero;

        /// <summary>
        /// Whether any part of <paramref name="plane"/>, grown by <paramref name="reach"/>, may lie inside
        /// <paramref name="frustum"/>. Conservative: false only when the grown box is provably outside one frustum
        /// plane. The plane and the frustum must be in the same space, which for the water pass is the render frame
        /// on both sides.
        /// </summary>
        public static bool MayBeVisible(in WaterPlane plane, Vector2 reach, in FrustumPlanes frustum)
        {
            float halfX = MathF.Abs(plane.HalfExtentX) + reach.X;
            float halfZ = MathF.Abs(plane.HalfExtentZ) + reach.X;
            return frustum.IntersectsAabb(
                new Vector3(plane.CenterX - halfX, plane.SurfaceY - reach.Y, plane.CenterZ - halfZ),
                new Vector3(plane.CenterX + halfX, plane.SurfaceY + reach.Y, plane.CenterZ + halfZ));
        }
    }
}
