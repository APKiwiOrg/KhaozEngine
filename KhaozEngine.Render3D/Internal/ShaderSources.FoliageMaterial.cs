using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Keep the shared lighting and frame declarations in one home. No material texture or discard is reachable.
    public static readonly string FoliageOpaqueFrag = FoliageOpaqueHeader() + @"
// Opaque no-map foliage material.
void main() {
    vec3 Ngeo = normalize(vNormalW);
    vec3 N = Ngeo;
    vec3 albedo = vColor.rgb * vec3(1.0) * vec3(1.0);
    float specStrength = 0.0 * (1.0 - 0.0);
    float specExp = max(mix(32.0, 8.0, 0.0), 1.0);
    float ndlKeyForShadow = max(dot(N, -normalize(LightDir.xyz)), 0.0);
    float keyShadow = sampleKeyShadow(ShadowMap, ShadowSamp, vWorldPos, Ngeo, ndlKeyForShadow);
    vec3 diffuse; vec3 specColor;
    computeLighting(PointShadowMap, PointShadowTransientMap, ShadowSamp, N, vWorldPos, specStrength, specExp, keyShadow, diffuse, specColor);
    vec3 lit = albedo * (indirectDiffuse(N, vWorldPos) + diffuse) + specColor;
    lit += vec3(0.0);
    lit = atmosphereFogKeyShadowed(lit, vWorldPos, RenderOrigin.w > 0.5, ShadowMap, ShadowSamp);
    oColor = vec4(lit, 1.0);
    oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0 - clamp(0.0, 0.0, 1.0));
    oDepth = vec4(gl_FragCoord.z, gl_FragCoord.z, gl_FragCoord.z, 1.0);
}";

    static string FoliageOpaqueHeader()
    {
        string header = ModelFrag[..ModelFrag.IndexOf("void main() {", StringComparison.Ordinal)]
            .Replace(InteriorCutGlsl, "", StringComparison.Ordinal);
        header = Regex.Replace(header, @"layout\(set=0, binding=(3|4|5|6|14)\)[^;]+;[^\r\n]*", "");
        return Regex.Replace(header, @"(?<=set=0, binding=)(7|8|9|10|11|12|13|15|16|17)(?=\))", match =>
        {
            int binding = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return (binding >= 15 ? binding - 5 : binding - 4).ToString(CultureInfo.InvariantCulture);
        });
    }

    // The RG16F motion target drops zw. Read every intervening interpolant only into its discarded alpha,
    // keeping FXC's signature contiguous without changing a stored component or the shared vertex interface.
    public static readonly string FoliageOpaqueMotionFrag = MotionFragment(FoliageOpaqueFrag, 11,
        sinkRead: "    oMotion.w += (vUv.x + vTint.x + vEmissive.x + vSpecParams.x + vTangent.x + vDynamic + vDissolve.x + vDissolveComplement) * 1e-30;\n");
}
