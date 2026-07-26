namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Where the water surface's displacement, normal and foam come from. The SHADING stack (body absorption,
    /// analytic sky reflection, GGX glint, foam colouring, shore fade) is identical either way - this only picks
    /// the source of the geometry underneath it.
    /// </summary>
    public enum WaterWaveSource
    {
        /// <summary>
        /// The closed-form procedural surface: a Gerstner swell in the vertex stage plus a golden-angle cosine
        /// ripple slope spectrum in the fragment stage. Everything shipped through 14.28.0, unchanged, and the
        /// default. Needs no compute support, costs no GPU memory, and is the fallback whenever
        /// <see cref="KhaozEngine.Gpu.GpuCapabilities.SupportsCompute"/> is false.
        /// </summary>
        Procedural = 0,

        /// <summary>
        /// A Tessendorf inverse-FFT ocean: a TMA (Texel-Marsen-Arsloe) directional spectrum evolved by the
        /// dispersion relation and inverse-transformed on the GPU every frame into displacement, slope and
        /// Jacobian-foam maps over two or three octave-separated cascades
        /// (<see cref="WaterSeaState"/>). Requires
        /// <see cref="KhaozEngine.Gpu.GpuCapabilities.SupportsCompute"/>; a device without it silently renders
        /// <see cref="Procedural"/> instead rather than failing.
        /// <para>
        /// Why it exists: a finite sum of directional components always keeps SOME residual regularity, and every
        /// round of hiding one coherent structure only let the eye find the next. An FFT grid sums a component per
        /// grid cell (16384 per cascade at the default resolution, three cascades) drawn from a real directional
        /// spectrum, so there is no small set of headings left to read as a pattern.
        /// </para>
        /// </summary>
        FftOcean = 1,
    }
}
