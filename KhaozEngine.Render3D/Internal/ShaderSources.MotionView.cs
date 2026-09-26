using System;
using System.Globalization;

namespace KhaozEngine.Render3D.Internal;

/// <summary>The MotionVectors debug view's fragment (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 5). Part of the
/// <see cref="ShaderSources"/> partial. It pairs with <see cref="FullscreenVert"/>.</summary>
internal static partial class ShaderSources
{
    /// <summary>The MotionVectors view's brightness is full at this many internal pixels of motion.</summary>
    internal const float MotionViewFullBrightnessPixels = 16f;

    /// <summary>Below this many internal pixels of motion the MotionVectors view reads a pixel as still.</summary>
    internal const float MotionViewStillPixels = 1.0e-4f;

    /// <summary>Hue for direction and brightness for length, full at <see cref="MotionViewFullBrightnessPixels"/>
    /// internal pixels, black for the background sentinel and for no motion. The background test is
    /// <see cref="MotionMath.IsBackground"/>, U alone past <see cref="MotionMath.BackgroundThreshold"/>, as every other
    /// reader tests it. The thresholds are spliced from those C# constants, so the shader cannot drift from
    /// them.</summary>
    public static readonly string MotionVectorsViewFrag = @"#version 450
layout(set=0, binding=0) uniform texture2D Motion;
layout(set=0, binding=1) uniform sampler Samp;
layout(location=0) in vec2 vUv;
layout(location=0) out vec4 oColor;
const float BackgroundThreshold = " + MotionViewFloat(MotionMath.BackgroundThreshold) + @";
const float FullBrightnessPixels = " + MotionViewFloat(MotionViewFullBrightnessPixels) + @";
const float StillPixels = " + MotionViewFloat(MotionViewStillPixels) + @";
const float TwoPi = 6.28318530718;
void main() {
    vec2 uv = vec2(vUv.x, 1.0 - vUv.y);   // the final blit's flip, as TransitionCrossfadeFrag
    vec2 motion = texture(sampler2D(Motion, Samp), uv).xy;
    vec2 pixels = motion * vec2(textureSize(sampler2D(Motion, Samp), 0));
    float magnitude = length(pixels);
    if (abs(motion.x) > BackgroundThreshold || magnitude < StillPixels) {
        oColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }
    float hue = atan(pixels.y, pixels.x) / TwoPi + 0.5;
    vec3 rgb = clamp(abs(fract(hue + vec3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0, 0.0, 1.0);
    oColor = vec4(rgb * clamp(magnitude / FullBrightnessPixels, 0.0, 1.0), 1.0);
}";

    // A GLSL float literal from a C# constant: round-trip digits in the invariant culture, always with a decimal point
    // or an exponent.
    static string MotionViewFloat(float value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal)
            ? text : text + ".0";
    }
}
