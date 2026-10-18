using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Archive-only visibility signal. Its targets and shader text exist only for a vis token at startup.</summary>
internal static class TemporalVisibilityDiagnostics
{
    internal const int Release10 = 1 << 24, Release20 = 1 << 25, Release30 = 1 << 26,
        Reset20 = 1 << 27, ShowRelease = 1 << 28;
    internal const int Mean = 1 << 29, DepthQualified = 1 << 30;
    internal const int Mask = Release10 | Release20 | Release30 | Reset20 | ShowRelease | Mean | DepthQualified;
    internal static GpuPixelFormat Format => (TemporalResolveDiagnostics.InitialMask & Mean) != 0
        ? GpuPixelFormat.R8UNorm : GpuPixelFormat.R32Float;

    internal static bool TryComparison(string token, out int mask)
    {
        int separator = token.LastIndexOf('-');
        string mode = separator < 0 ? "" : token[..separator];
        bool centre = mode == "vis-centre-dq";
        bool large = mode == "vis-mean-dq-large";
        mask = 0;
        if (mode != "vis-mean" && mode != "vis-mean-dq" && !centre && !large) return false;
        int threshold = token[(separator + 1)..] switch
        {
            "0.1" => Release10,
            "0.15" => Release30,
            "0.2" => Release20,
            _ => throw new InvalidOperationException("Unknown visibility comparison threshold."),
        };
        mask = Mean | threshold | TemporalResolveDiagnostics.ShadowBilinear3x3;
        if (token.Contains("-dq-", StringComparison.Ordinal)) mask |= DepthQualified;
        if (large)
        {
            if (threshold != Release10) throw new InvalidOperationException("The large-change arm uses threshold 0.1.");
            mask |= Release20;
        }
        if (centre) mask |= Reset20; // In a mean comparison this flag selects centre, never reset.
        return true;
    }
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

    internal static string PreserveFragment(string program)
    {
        if (!Enabled) return program;
        program = ShaderText.After(program, "layout(location=2) out vec4 oDepth;",
            "\nlayout(location=3) out float oKeyVisibility;");
        return ShaderText.BeforeEndOfMain(program, "    oKeyVisibility = 0.0;\n");
    }

    internal static string ResolveFragment(string program)
    {
        if (!Enabled) return program;
        program = program.Replace("\r\n", "\n", StringComparison.Ordinal);
        bool split = program.Contains("uniform texture2D PreparedEdge;", StringComparison.Ordinal);
        int binding = split ? 12 : 9;
        string declarations = $"#define TemporalVisibility\nlayout(set=0, binding={binding}) uniform texture2D KeyVisibility;\n"
            + $"layout(set=0, binding={binding + 1}) uniform texture2D PrevVisibility;\n"
            + $"layout(set=0, binding={binding + 2}) uniform VisibilityHistory {{ vec4 PrevJitter; }};\n";
        if ((TemporalResolveDiagnostics.InitialMask & (Mean | Release10 | Release20)) == (Mean | Release10 | Release20))
            declarations += "#define TemporalVisibilityLarge\n";
        return ShaderText.After(program, "    vec4 Params;\n};", "\n" + declarations);
    }
}
