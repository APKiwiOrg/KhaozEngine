namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Texture filtering for a <see cref="SpriteBatch"/> Begin..End pass. <see cref="Linear"/> (the default)
    /// smooths under scaling. <see cref="Point"/> is nearest-neighbour for crisp pixel art.
    /// <para>
    /// Both modes sample through the device's shared <c>LinearSampler</c> and <c>PointSampler</c>, which are
    /// WRAP-addressed by contract (see <c>IGpuDevice.PointSampler</c>), never clamped. A filtered tap past a
    /// source rectangle's edge therefore reads the texels beyond it, or the opposite edge of the texture. An
    /// unpadded image or atlas entry drawn at a fractional scale can bleed at its border. Pad the entry, repeat
    /// its border texels, or draw it at integer scale with <see cref="Point"/>.
    /// </para>
    /// </summary>
    public enum SamplerMode
    {
        /// <summary>Bilinear filtering (smooth). The default.</summary>
        Linear,

        /// <summary>Nearest-neighbour filtering (crisp pixels, no blur).</summary>
        Point,
    }
}
