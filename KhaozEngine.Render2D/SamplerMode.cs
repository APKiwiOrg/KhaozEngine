namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Texture filtering for a <see cref="SpriteBatch"/> Begin..End pass. <see cref="Linear"/> (the default)
    /// smooths under scaling; <see cref="Point"/> is nearest-neighbour for crisp pixel art (the 4.x
    /// <c>SamplerState.PointClamp</c> equivalent).
    /// </summary>
    public enum SamplerMode
    {
        /// <summary>Bilinear filtering (smooth). The default.</summary>
        Linear,

        /// <summary>Nearest-neighbour filtering (crisp pixels, no blur).</summary>
        Point,
    }
}
