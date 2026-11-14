namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // The shipped five-tap kernel, shared by both grids without changing its emitted text.
    internal const string TemporalHistorySampleGlsl = @"vec4 sampleHistoryCatmullRom(vec2 uv, vec2 size) {
    vec2 samplePos = uv * size;
    vec2 texPos1 = floor(samplePos - 0.5) + 0.5;
    vec2 f = samplePos - texPos1;
    vec2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    vec2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    vec2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    vec2 w3 = f * f * (-0.5 + 0.5 * f);
    vec2 w12 = w1 + w2;
    vec2 tc0 = (texPos1 - 1.0) / size;
    vec2 tc3 = (texPos1 + 2.0) / size;
    vec2 tc12 = (texPos1 + w2 / w12) / size;
    vec4 top = textureLod(sampler2D(HistoryColor, LinearClamp), vec2(tc12.x, tc0.y), 0.0);
    vec4 left = textureLod(sampler2D(HistoryColor, LinearClamp), vec2(tc0.x, tc12.y), 0.0);
    vec4 middle = textureLod(sampler2D(HistoryColor, LinearClamp), tc12, 0.0);
    vec4 right = textureLod(sampler2D(HistoryColor, LinearClamp), vec2(tc3.x, tc12.y), 0.0);
    vec4 bottom = textureLod(sampler2D(HistoryColor, LinearClamp), vec2(tc12.x, tc3.y), 0.0);
    vec4 sum = top * (w12.x * w0.y) + left * (w0.x * w12.y) + middle * (w12.x * w12.y) + right * (w3.x * w12.y)
        + bottom * (w12.x * w3.y);
    float weight = w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y;
    ivec2 base = ivec2(floor(samplePos - 0.5));
    ivec2 lastTexel = ivec2(size) - ivec2(1);
    vec4 t00 = texelFetch(sampler2D(HistoryColor, LinearClamp), clamp(base, ivec2(0), lastTexel), 0);
    vec4 t10 = texelFetch(sampler2D(HistoryColor, LinearClamp), clamp(base + ivec2(1, 0), ivec2(0), lastTexel), 0);
    vec4 t01 = texelFetch(sampler2D(HistoryColor, LinearClamp), clamp(base + ivec2(0, 1), ivec2(0), lastTexel), 0);
    vec4 t11 = texelFetch(sampler2D(HistoryColor, LinearClamp), clamp(base + ivec2(1, 1), ivec2(0), lastTexel), 0);
    vec4 texelMin = min(min(t00, t10), min(t01, t11));
    vec4 texelMax = max(max(t00, t10), max(t01, t11));
    return clamp(sum / weight, texelMin, texelMax);
}
";
}
