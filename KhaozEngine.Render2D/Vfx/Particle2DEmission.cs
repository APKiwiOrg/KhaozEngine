namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>How a 2D particle's initial velocity direction is chosen at emit time.</summary>
    public enum Particle2DEmission
    {
        /// <summary>Random direction over the full circle (outward burst).</summary>
        Radial,

        /// <summary>Along <see cref="Particle2DEmitterConfig.Direction"/>, jittered by the spread cone.</summary>
        Directional,
    }
}
