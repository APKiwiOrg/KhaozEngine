using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Archive-only visibility signal. Its targets and shader text exist only for a vis token at startup.</summary>
internal static class TemporalVisibilityDiagnostics
{
    internal const int Release10 = 1 << 24, Release20 = 1 << 25, Release30 = 1 << 26,
        Reset20 = 1 << 27, ShowRelease = 1 << 28;
    internal const int Mask = Release10 | Release20 | Release30 | Reset20 | ShowRelease;
    internal const GpuPixelFormat Format = GpuPixelFormat.R32Float;
    internal static bool Enabled => (TemporalResolveDiagnostics.InitialMask & Mask) != 0;

    internal static string LitFragment(string program)
    {
        if (!Enabled) return program;
        program = ShaderText.After(program, "layout(location=2) out vec4 oDepth;",
            "\nlayout(location=3) out float oKeyVisibility;");
        return ShaderText.After(program,
            "float keyShadow = sampleKeyShadow(ShadowMap, ShadowSamp, vWorldPos, Ngeo, ndlKeyForShadow);",
            "\n    oKeyVisibility = keyShadow;");
    }

    internal static string ResolveFragment(string program)
    {
        if (!Enabled) return program;
        bool split = program.Contains("uniform texture2D PreparedEdge;", StringComparison.Ordinal);
        int binding = split ? 12 : 9;
        string declarations = $"#define TemporalVisibility\nlayout(set=0, binding={binding}) uniform texture2D KeyVisibility;\n"
            + $"layout(set=0, binding={binding + 1}) uniform texture2D PrevVisibility;\n"
            + $"layout(set=0, binding={binding + 2}) uniform VisibilityHistory {{ vec4 PrevJitter; }};\n";
        return ShaderText.After(program, "    vec4 Params;\n};", "\n" + declarations);
    }
}
