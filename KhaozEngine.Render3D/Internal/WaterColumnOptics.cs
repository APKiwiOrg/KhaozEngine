using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D.Internal;

/// <summary>CPU reference for the water adaptation of Hapke's isotropic multiple-scattering approximation.</summary>
/// <remarks>
/// The Chandrasekhar H-function approximation assumes a homogeneous semi-infinite medium, with no opposition
/// or particulate-packing term. This is an estimate, not an exact water transport solution.
/// Sky is one unshadowed effective zenith source with RGB Post.AmbientColor. The fill light is excluded.
/// Reference light is linear white LightColor and AmbientColor, sun at zenith and a straight-down view with
/// zero Fresnel. Engine irradiance is pi times LightColor, so each source contributes a/8 single scattering.
/// Adding the H(a,mu)*H(a,mu0)-1 multiple-scattering factor gives F(a) = a*H(a,1)^2/4 in reference light.
/// See <see href="https://doi.org/10.1029/JB086iB04p03039">Hapke's analytic model</see> and
/// <see href="https://doi.org/10.1029/2025JE009415">the H-function and isotropic terms, equations 2 and 4</see>.
/// Keep the shader H-function and reference normalization matched to this helper.
/// </remarks>
internal static class WaterColumnOptics
{
    internal const float MaximumReferenceColor = 2.25f;

    /// <summary>Derive coefficients from already validated RGB in [0,1] and finite clarity at least 0.001 m.
    /// Alpha is ignored. Each colour channel is reachable below unit albedo, so no colour cap is needed.</summary>
    internal static WaterColumnCoefficients Derive(Color waterColor, float clarity)
    {
        double meanColor = ((double)waterColor.R + waterColor.G + waterColor.B) / 3d;
        double meanExtinction = 3d / clarity;
        var extinction = new Vector3(
            (float)(meanExtinction * (1d + 0.5d * (meanColor - waterColor.R))),
            (float)(meanExtinction * (1d + 0.5d * (meanColor - waterColor.G))),
            (float)(meanExtinction * (1d + 0.5d * (meanColor - waterColor.B))));
        var albedo = new Vector3(
            InvertReferenceDeepRadiance(waterColor.R),
            InvertReferenceDeepRadiance(waterColor.G),
            InvertReferenceDeepRadiance(waterColor.B));
        Vector3 scattering = albedo * extinction;
        return new(extinction, scattering, extinction - scattering, albedo);
    }

    /// <summary>Deep RGB radiance under the reference sun and sky, F(a) = a*H(a,1)^2/4.</summary>
    internal static Vector3 ReferenceDeepRadiance(Vector3 albedo) => new(
        (float)ReferenceDeepRadiance(albedo.X),
        (float)ReferenceDeepRadiance(albedo.Y),
        (float)ReferenceDeepRadiance(albedo.Z));

    /// <summary>H(a,mu) = (1+2*mu)/(1+2*mu*sqrt(1-a)), for albedo and zenith cosine in [0,1].</summary>
    internal static float H(float albedo, float mu) => (float)H((double)albedo, mu);

    static double H(double albedo, double mu) => (1d + 2d * mu) / (1d + 2d * mu * Math.Sqrt(1d - albedo));

    static double ReferenceDeepRadiance(double albedo)
    {
        double h = H(albedo, 1d);
        return albedo * h * h / 4d;
    }

    static float InvertReferenceDeepRadiance(double color)
    {
        if (color == 0d) return 0f;

        double lower = 0d;
        double upper = 1d;
        for (int step = 0; step < 32; step++)
        {
            double midpoint = (lower + upper) / 2d;
            if (ReferenceDeepRadiance(midpoint) < color) lower = midpoint;
            else upper = midpoint;
        }
        return (float)((lower + upper) / 2d);
    }
}
