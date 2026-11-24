using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal;

/// <summary>The optional last opaque attachment. Its RGB is fog-transmitted indirect diffuse,
/// and its alpha marks opaque coverage even where the receiver has no ambient illumination.</summary>
internal static class AmbientDiffuseTarget
{
    internal const GpuPixelFormat Format = GpuPixelFormat.R16G16B16A16Float;

    internal static bool IsAttached(in GpuOutputDescription outputs) =>
        outputs.Colour.Length > 3 && outputs.Colour[^1] == Format;

    internal static string Fragment(string fragment, int slot)
    {
        // Evaluate the same indirect term once for both results. The shared fog implementation returns
        // the exact transmission it uses to shade the fragment, including vacuum and disabled paths.
        const string indirect = "vec3 lit = albedo * (indirectDiffuse(";
        int start = fragment.IndexOf(indirect, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException("The opaque shader does not compute indirect diffuse lighting.");
        int expressionStart = start + indirect.Length;
        int end = fragment.IndexOf(") + diffuse)", expressionStart, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException("The opaque shader does not combine indirect and direct diffuse lighting.");
        string args = fragment[expressionStart..end];
        fragment = string.Concat(fragment.AsSpan(0, start),
            $"vec3 ambientDiffuse = indirectDiffuse({args});\n    vec3 lit = albedo * (ambientDiffuse",
            fragment.AsSpan(end + 1));
        fragment = ShaderText.After(fragment, "layout(location=2) out vec4 oDepth;",
            $"\nlayout(location={slot}) out vec4 oAmbientDiffuse;");
        fragment = fragment.Replace(
            "bool enabled, texture2D shadowAtlas, sampler shadowSamp) {\n    if (!enabled) return colour;\n    vec3 transmission",
            "bool enabled, texture2D shadowAtlas, sampler shadowSamp, out vec3 transmission) {\n    if (!enabled) { transmission = vec3(1.0); return colour; }\n    transmission",
            StringComparison.Ordinal);
        const string fog = "lit = atmosphereFogKeyShadowed(lit, vWorldPos, RenderOrigin.w > 0.5, ShadowMap, ShadowSamp);";
        fragment = ShaderText.After(fragment, fog, "\n    oAmbientDiffuse = vec4(albedo * ambientDiffuse * transmission, 1.0);");
        return fragment.Replace(fog, "vec3 transmission;\n    " + fog.Replace("ShadowSamp);", "ShadowSamp, transmission);",
            StringComparison.Ordinal), StringComparison.Ordinal);
    }
}
