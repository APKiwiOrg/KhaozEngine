using System;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Test-owned arithmetic from e5c4126c3, before the pixel-identical shader cuts.</summary>
internal static class ShaderCutBaseline
{
    static readonly string TileSamples = string.Join("\n", new[]
    {
        "    vec3 albedo = vec3(0.0);",
        "    for (int L = 0; L < 5; L++) {",
        "        float wl = w[L];",
        "        if (wl <= 0.001) continue;",
        "        int sl = slot[L];",
        "        vec3 c = sampleMaterial(sl, wpAbs, dWx, dWy, tw, tri);",
        "        // The material's steep companion, a property of the MATERIAL whatever slot role it fills here. Its own",
        "        // companion is never followed. A material with none, or a slope below its band, takes no extra tap.",
        "        vec4 sb = SteepBand[sl];",
        "        if (sb.x > 0.5) {",
        "            float s = 1.0 - smoothstep(sb.y, sb.z, ny);",
        "            if (s > 0.0) {",
        "                c = mix(c, sampleMaterial(clamp(int(sb.x - 0.5), 0, maxSlot), wpAbs, dWx, dWy, tw, tri), s);",
        "            }",
        "        }",
        "        albedo += wl * c;",
        "    }",
        "",
    });

    internal static string RestoreTileSamples(string source) => ReplaceRegion(source,
        "    // Repeated active roles", "    // The per-vertex jitter", TileSamples);

    static string ReplaceRegion(string source, string first, string next, string baseline)
    {
        int start = source.IndexOf(first, StringComparison.Ordinal);
        if (start < 0) return source;
        int end = source.IndexOf(next, start, StringComparison.Ordinal);
        if (end < 0 || source.IndexOf(first, start + first.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The shader baseline needs exactly one complete region.");
        return source[..start] + baseline + source[end..];
    }
}
