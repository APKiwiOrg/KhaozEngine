using System;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

// Shared depth inputs for the A/B tolerance-edge cases after withdrawal of the pass-one mask.
internal sealed class TemporalVisibilityDepthInputs : TemporalResolveGpuFacts
{
    /// <summary>The NDC depth of a neighbour at the centre's current tolerance: the view depth
    /// <paramref name="baseDepth"/> times one plus or minus <see cref="TemporalResolveTuning.VisibilityDepthTolerance"/>,
    /// moved <paramref name="ulps"/> float steps. The A/B fact's tolerance-edge tiles use it too.</summary>
    internal static float ToleranceEdge(double baseDepth, bool farther, int ulps)
        => Ulps(Ndc(baseDepth * (farther ? 1.0 + TemporalResolveTuning.VisibilityDepthTolerance
            : 1.0 - TemporalResolveTuning.VisibilityDepthTolerance)), ulps);

    // The NDC depth the resolve's perspective depth parameters turn into this view depth.
    static float Ndc(double linear)
    {
        double near = Depth.Y, far = Depth.Z;
        return (float)((far - near * far / linear) / (far - near));
    }

    static float Ulps(float v, int k) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(v) + k);
}
