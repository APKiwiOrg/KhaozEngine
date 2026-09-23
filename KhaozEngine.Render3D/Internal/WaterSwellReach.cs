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
    }
}
