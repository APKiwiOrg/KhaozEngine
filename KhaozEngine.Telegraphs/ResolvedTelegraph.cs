using KhaozEngine.Primitives;

namespace KhaozEngine.Telegraphs
{
    /// <summary>
    /// The concrete per-frame visual a renderer draws: final fill/outline colors (alphas already multiplied by
    /// opacity + pulse), the swept fill fraction (0..1 of the shape's extent that is filled this frame), an
    /// additive impact-flash term (0..1), and the blend/fill mode carried through from the style. Produced by
    /// <see cref="TelegraphResolve.Resolve"/>; holds no shape geometry (the renderer applies
    /// <see cref="FillFraction"/> to the shape).
    /// </summary>
    public readonly struct ResolvedTelegraph
    {
        public readonly Color FillColor;
        public readonly Color OutlineColor;
        public readonly float FillFraction;
        public readonly float FlashAdd;
        public readonly float EdgeThickness;
        public readonly FillMode FillMode;
        public readonly TelegraphBlend Blend;

        public ResolvedTelegraph(Color fillColor, Color outlineColor, float fillFraction, float flashAdd,
            float edgeThickness, FillMode fillMode, TelegraphBlend blend)
        {
            FillColor = fillColor;
            OutlineColor = outlineColor;
            FillFraction = fillFraction;
            FlashAdd = flashAdd;
            EdgeThickness = edgeThickness;
            FillMode = fillMode;
            Blend = blend;
        }
    }
}
