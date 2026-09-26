using System.Globalization;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// The temporal variants of the model-pass fragments (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 4). Each is its base
/// program byte for byte plus three things: the two clip-position interpolants its vertex stage adds, the fourth colour
/// output, and one write at the end of main. Building them from the base text keeps a lighting edit single-place, and
/// the byte-equality hash tables show every base program unchanged. Part of the <see cref="ShaderSources"/> partial.
/// </summary>
internal static partial class ShaderSources
{
    /// <summary>The line every model-pass fragment declares its third output with. The motion declarations follow it.</summary>
    const string DepthOutputGlsl = "layout(location=2) out vec4 oDepth;";

    /// <summary>The motion write every opaque variant ends main with, <see cref="MotionMath.UvMotion"/> in GLSL. One line
    /// with no comment, so <c>MotionShaderTextTests</c> can strip it and get the base program back.</summary>
    const string MotionWriteGlsl =
        "    oMotion = vec4((vCurClip.xy / vCurClip.w - vPrevClip.xy / vPrevClip.w) * vec2(0.5, -0.5), 0.0, 1.0);\n";

    /// <summary>
    /// The temporal variant of an opaque model-pass fragment. <paramref name="firstLocation"/> is where the paired motion
    /// vertex emits <c>vCurClip</c>, with <c>vPrevClip</c> one above. <paramref name="sinkDeclaration"/> and
    /// <paramref name="sinkRead"/> keep alive a location below the pair that the base fragment does not read, at a 1e-30
    /// weight on the alpha channel the RG16F target drops, so the pixel-input signature stays gap-free for FXC
    /// (docs/CROSS-PLATFORM.md). The sink read contains <c>oMotion</c>, so the text test strips it with the rest.
    /// </summary>
    internal static string MotionFragment(string fragment, int firstLocation, string sinkDeclaration = "",
        string sinkRead = "") =>
        ShaderText.BeforeEndOfMain(
            ShaderText.After(fragment, DepthOutputGlsl,
                "\nlayout(location=3) out vec4 oMotion;"
                + "\nlayout(location=" + firstLocation.ToString(CultureInfo.InvariantCulture) + ") in vec4 vCurClip;"
                + "\nlayout(location=" + (firstLocation + 1).ToString(CultureInfo.InvariantCulture) + ") in vec4 vPrevClip;"
                + sinkDeclaration),
            MotionWriteGlsl + sinkRead);

    /// <summary>ModelFrag with motion. ModelFrag reads every interpolant 0 to 10, so the pair sits at 11 and 12. Pairs
    /// with <see cref="ModelMotionVert"/>, and in later tasks with the CPU-skinned and foliage motion vertices, which
    /// emit the same block.</summary>
    public static readonly string ModelMotionFrag = MotionFragment(ModelFrag, 11);

    /// <summary>SkinnedModelFrag with motion. The base fragment reads 0 to 8 and the vertex emits vDissolve at 9, so a
    /// sink holds 9 live below the pair at 10 and 11.</summary>
    public static readonly string SkinnedModelMotionFrag = MotionFragment(SkinnedModelFrag, 10,
        "\nlayout(location=9) in vec2 vDissolve;", "    oMotion.w += vDissolve.x * 1e-30;\n");

    /// <summary>SkinnedModelDissolveFrag with motion. The base reads 0 to 9, so the pair follows at 10 and 11.</summary>
    public static readonly string SkinnedModelDissolveMotionFrag = MotionFragment(SkinnedModelDissolveFrag, 10);
}
