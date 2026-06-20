namespace KhaozEngine.Render2D
{
    /// <summary>
    /// How a <see cref="SpriteBatch"/> draw composites with what is already in the target. Set via
    /// <see cref="SpriteBatch.BlendMode"/>; can change mid-batch (per quad) without a new <c>Begin</c>, and
    /// painter's order is preserved across blend modes. <see cref="Alpha"/> is the default (standard
    /// source-over transparency); <see cref="Additive"/> sums light for glows, sparks, beams and flashes.
    /// </summary>
    public enum BlendMode
    {
        /// <summary>Standard source-over alpha blending (the default).</summary>
        Alpha,

        /// <summary>Additive blending (source colour added to the target) - glowy VFX.</summary>
        Additive,
    }
}
