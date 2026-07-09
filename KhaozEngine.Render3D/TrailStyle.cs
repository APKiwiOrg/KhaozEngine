using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>How a trail strip composites into the model pass.</summary>
    public enum TrailBlend
    {
        /// <summary>Additive (src.a / 1): glow accumulates, ideal for energy swings, thruster streaks, tracers.</summary>
        Additive,

        /// <summary>Standard alpha (src.a / 1-src.a): a physical smear (dust, a dark blade-wipe). Within one strip
        /// the samples are tail-&gt;head ordered so it self-composites correctly; overlapping separate alpha trails
        /// are not depth-sorted against each other.</summary>
        Alpha,
    }

    /// <summary>
    /// Look tunables for a motion trail (see <see cref="Scene3D.DrawTrail"/>). Immutable; derive variants with
    /// <c>with</c> (the idiom is <c>TrailStyle.Default with { ... }</c>). <see cref="Color"/> tints the whole strip
    /// and its alpha multiplies each <see cref="TrailSample.Alpha"/> (so the tail fade lives in the samples, an
    /// overall opacity in the style). <see cref="SoftEdge"/> softens the across-width falloff in the fragment shader.
    /// </summary>
    public readonly record struct TrailStyle
    {
        /// <summary>Strip tint (RGBA). Its alpha multiplies each sample's alpha. Default opaque white.</summary>
        public Color Color { get; init; }

        /// <summary>Additive (default) or alpha compositing.</summary>
        public TrailBlend Blend { get; init; }

        /// <summary>Across-width edge softness in [0,1]: 0 = hard edges, 1 = fully feathered to the centre.
        /// Default 0.5.</summary>
        public float SoftEdge { get; init; }

        /// <summary>A sensible starting point: opaque white, additive, a half-feathered edge.</summary>
        public static TrailStyle Default => new()
        {
            Color = Color.White,
            Blend = TrailBlend.Additive,
            SoftEdge = 0.5f,
        };
    }
}
