using System;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The RECEIVER GLSL for point-shadow filtering, read as text: that the Hard path is still the four-tap compare it
/// shipped as, and that the Soft path samples by DIRECTION rather than by a clamped offset inside one cell.
/// <para>
/// THE HARD PIN IS A BYTE-IDENTITY GATE STANDING IN FOR A PICTURE ONE. A capture hash would say the same thing
/// more directly, but every hash is per backend and this suite has no per-backend pin to hang one on: its whole
/// design is relative probes and no goldens, so a Metal-only hash would be a row that quietly measures nothing on
/// two of the three golden legs. The formula is what actually has to stay put, it is the same formula on every
/// backend, and pinning its TEXT holds on all three legs and on a plain headless run as well.
/// </para>
/// </summary>
public sealed class PointShadowFilterShaderTests
{
    /// <summary>
    /// The four-tap compare EXACTLY as it stood before the soft filter landed, copied out of the shipped source at
    /// that commit. Not a paraphrase and not a re-derivation: a reformat here would pass a test that exists to
    /// catch a reformat there. The soft path shares no line of it, deliberately, because the cheapest way to move
    /// the Hard picture is to factor a helper out of it and have the helper grow a line for the other mode.
    /// </summary>
    const string FourTapCompare = @"
    float face; vec2 uv;
    pointShadowFace(-toL, face, uv);
    vec2 texel = PointShadowAtlas.xy;                      // one ATLAS texel, in atlas UV
    vec2 cellSize = vec2(1.0 / PointShadowAtlas.w, 1.0 / max(PointShadowAtlas.z, 1.0));
    vec2 cellMin = vec2(face, params.x) * cellSize;
    vec2 base = cellMin + uv * cellSize;
    vec2 lo = cellMin + texel * 0.5;
    vec2 hi = cellMin + cellSize - texel * 0.5;
    float d = dist / max(radius, 1e-6);
    float bias = params.y + params.z * (1.0 - ndlRaw);
    float lit = 0.0;
    for (int oy = 0; oy < 2; oy++) {
        for (int ox = 0; ox < 2; ox++) {
            vec2 tap = clamp(base + (vec2(float(ox), float(oy)) - 0.5) * texel, lo, hi);
            float stored = texture(sampler2D(atlas, samp), tap).r;
            lit += step(d, stored + bias);                 // receiver nearer than the stored caster => lit
        }
    }
    return lit * 0.25;
";

    [Fact]
    public void TheHardPathIsStillTheFourTapCompareItShippedAs()
    {
        Assert.Contains(FourTapCompare, ShaderSources.LightingCommonGlsl, StringComparison.Ordinal);
    }

    /// <summary>The mode is read off the frame block rather than compiled in, so one program serves both filters
    /// and a settings change needs no pipeline rebuild.</summary>
    [Fact]
    public void TheFilterIsBranchedOnTheUniformRatherThanCompiledIn()
    {
        Assert.Contains("PointShadowFilter.x < 0.5", ShaderSources.LightingCommonGlsl, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE SOFT PATH SAMPLES BY DIRECTION, which is the whole reason a wider kernel does not seam at a cube face
    /// boundary: every tap runs the face select for itself, so a tap that leaves the +Z face lands on the +X one
    /// rather than on the clamped edge of the cell it started in.
    /// </summary>
    [Fact]
    public void TheSoftPathRunsTheFaceSelectPerTap()
    {
        Assert.Contains("float pointShadowDepthAt(", ShaderSources.LightingCommonGlsl, StringComparison.Ordinal);
        // The blocker search and the filter both go through it, and neither reconstructs a UV of its own.
        Assert.Contains("pointShadowBlockerSearch(", ShaderSources.LightingCommonGlsl, StringComparison.Ordinal);
    }

    /// <summary>The per-fragment rotation is hashed off the ABSOLUTE world position (the render-frame one plus the
    /// render origin), so the dither pattern is nailed to the world and does not crawl over a surface as the
    /// camera moves or as a floating origin shifts under it. Both halves are folded into a cell BEFORE they are
    /// summed, so a world far from zero keeps the hash's precision instead of banding the penumbra.</summary>
    [Fact]
    public void ThePerFragmentRotationIsHashedOffTheAbsoluteWorldPosition()
    {
        Assert.Contains("fract(fract(worldPos * 0.0625) + fract(RenderOrigin.xyz * 0.0625))",
            ShaderSources.LightingCommonGlsl, StringComparison.Ordinal);
        // The unfolded sum is the form that loses the hash a hundred kilometres out.
        Assert.DoesNotContain("worldPos + RenderOrigin.xyz", ShaderSources.LightingCommonGlsl,
            StringComparison.Ordinal);
    }

    /// <summary>A fragment standing on the light has no direction to sample by, and a zero direction would put a
    /// NaN basis through every tap. The soft path answers lit before it builds one.</summary>
    [Fact]
    public void AFragmentOnTheLightItselfIsLitWithoutSampling()
    {
        Assert.Contains("if (dist <= 1e-4) return 1.0;", ShaderSources.LightingCommonGlsl,
            StringComparison.Ordinal);
    }

    /// <summary>A fragment outside a light's radius leaves the loop before normalization, cel lighting, shadow
    /// sampling or specular work.</summary>
    [Fact]
    public void AFragmentOutsideTheLightsRadiusSamplesNothing()
    {
        string source = ShaderSources.LightingCommonGlsl;
        int reject = source.IndexOf("if (distSquared >= radius * radius) continue;", StringComparison.Ordinal);
        int normalize = source.IndexOf("vec3 L = (dist > 1e-4)", StringComparison.Ordinal);
        int sample = source.IndexOf("att *= samplePointShadow", StringComparison.Ordinal);
        int specular = source.IndexOf("vec3 Hp = normalize(L + V);", StringComparison.Ordinal);
        Assert.True(reject >= 0, "the squared-radius rejection is missing");
        Assert.True(reject < normalize, "radius rejection must precede light-vector normalization");
        Assert.True(reject < sample, "radius rejection must precede point-shadow sampling");
        Assert.True(reject < specular, "radius rejection must precede point-light specular work");
    }
}
