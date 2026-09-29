using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    [CollectionDefinition("TemporalEntryIdentitySerial", DisableParallelization = true)]
    public sealed class TemporalEntryIdentitySerialCollection { }

    // SCRATCH PROBE, never merged: the identity fact under rewrites of the temporal programs.
    [Collection("TemporalEntryIdentitySerial")]
    public sealed class TemporalEntryIdentityProbeGpuTests(ITestOutputHelper output)
    {
        const string Prepared = @"vec4 temporalPrepared(vec3 ycc, float reactiveDifference) {
    return vec4(unpackHalf2x16(packHalf2x16(ycc.xy)), unpackHalf2x16(packHalf2x16(vec2(ycc.z, reactiveDifference))));
}";

        static string IntHalf(bool flush) => @"float probeHalf(float v) {
    uint bits = floatBitsToUint(v);
    uint magnitude = bits & 0x7fffffffu;
    uint rounded;
    if (magnitude < 0x38800000u) {
        rounded = " + (flush ? "0u" : "floatBitsToUint(roundEven(uintBitsToFloat(magnitude) * 16777216.0) * 5.9604644775390625e-8)") + @";
    } else {
        rounded = (magnitude + 0xfffu + ((magnitude >> 13) & 1u)) & 0xffffe000u;
    }
    return uintBitsToFloat(rounded | (bits & 0x80000000u));
}
vec4 temporalPrepared(vec3 ycc, float reactiveDifference) {
    return vec4(probeHalf(ycc.x), probeHalf(ycc.y), probeHalf(ycc.z), probeHalf(reactiveDifference));
}";

        static readonly (string Old, string New)[] PrecisePrep =
        [
            ("float temporalLuma(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }",
                "float temporalLuma(vec3 c) { precise float l = dot(c, vec3(0.2126, 0.7152, 0.0722)); return l; }"),
            ("vec3 toWeighted(vec3 c) { return c / (1.0 + temporalLuma(c)); }",
                "vec3 toWeighted(vec3 c) { precise vec3 w = c / (1.0 + temporalLuma(c)); return w; }"),
            (@"vec3 rgbToYCoCg(vec3 c) {
    return vec3(",
                @"vec3 rgbToYCoCg(vec3 c) {
    precise vec3 y = vec3("),
            ("-0.25 * c.r + 0.5 * c.g - 0.25 * c.b);\n}", "-0.25 * c.r + 0.5 * c.g - 0.25 * c.b);\n    return y;\n}"),
            ("    return abs(temporalLuma(weightedColor) - temporalLuma(temporalWeighted(opaqueColor)));",
                "    precise float d = abs(temporalLuma(weightedColor) - temporalLuma(temporalWeighted(opaqueColor)));\n"
                + "    return d;"),
        ];

        static readonly (string Old, string New)[] InlineGather =
        [
            (@"            lumas[(y + 1) * 3 + (x + 1)] = prepared.x;
            temporalGather(neighbourhood, kernels, x + 1, y + 1, prepared.xyz, alpha, prepared.w);",
                @"            vec4 inl = temporalPrepared(rgbToYCoCg(temporalWeighted(texelFetch(sampler2D(SceneColor, LinearClamp),
                temporalNeighbourTexel(centreTexel, x, y, maxTexel), 0).rgb)), prepared.w);
            lumas[(y + 1) * 3 + (x + 1)] = inl.x;
            temporalGather(neighbourhood, kernels, x + 1, y + 1, inl.xyz, alpha, inl.w);"),
        ];

        static List<(string Old, string New)> Edits(string variant) => variant switch
        {
            "inline-gather" => [.. InlineGather],
            "int-rtne" => [(Prepared, IntHalf(false))],
            "int-rtne-ftz" => [(Prepared, IntHalf(true))],
            "precise-prep" => [.. PrecisePrep],
            "int-rtne-precise" => [(Prepared, IntHalf(false)), .. PrecisePrep],
            _ => throw new ArgumentException(variant),
        };

        public static TheoryData<string, string> Cross => new()
        {
            { "Fused", "int-rtne" }, { "Split", "int-rtne" }, { "Fused", "int-rtne-ftz" }, { "Split", "int-rtne-ftz" },
            { "Fused", "precise-prep" }, { "Split", "precise-prep" },
        };

        // One entry, the shipped programs against a rewrite of them: which side of the entries the rewrite moves.
        [GpuTheory]
        [MemberData(nameof(Cross))]
        public void CrossProbe(string entryName, string variant)
        {
            TemporalResolveEntry entry = Enum.Parse<TemporalResolveEntry>(entryName);
            List<(string Old, string New)> edits = Edits(variant);
            foreach (TemporalUpscale preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality })
            {
                var walkA = new PerspectiveFollowLines(320, 180, 2f, preset, 0.375f);
                var walkB = new PerspectiveFollowLines(320, 180, 2f, preset, 0.375f);
                using var a = new TemporalFixture(320, 180, s => walkA.Setup(s, preset));
                using var b = new TemporalFixture(320, 180, s => walkB.Setup(s, preset));
                a.Scene.TemporalResolveEntryForTests = entry;
                b.Scene.TemporalResolveEntryForTests = entry;
                int frames = TemporalFollowLinesRuns.Last + 9, colourValues = 0, stateValues = 0, firstFrame = -1;
                int colourSteps = 0, stateSteps = 0, rewritten = 0;
                for (int n = 0; n < frames; n++)
                {
                    a.Frame(Draw(walkA));
                    if (n == 0)
                    {
                        ShaderSources.TemporalRewriteForTests = glsl =>
                        {
                            string before = glsl;
                            foreach ((string o, string w) in edits) glsl = glsl.Replace(o, w, StringComparison.Ordinal);
                            if (!ReferenceEquals(before, glsl) && before != glsl) rewritten++;
                            return glsl;
                        };
                    }
                    try { b.Frame(Draw(walkB)); }
                    finally { ShaderSources.TemporalRewriteForTests = null; }
                    if (!a.Scene.ResolvedLastRenderForTests) continue;
                    Assert.Equal(entry, a.Scene.TemporalResolveRendererForTests?.LastEntry);
                    Assert.Equal(entry, b.Scene.TemporalResolveRendererForTests?.LastEntry);
                    float[] ca = TemporalTextureIo.Read(a.Device, a.Scene.TemporalHistory.Color(a.Scene.TemporalHistory.WriteIndex));
                    float[] cb = TemporalTextureIo.Read(b.Device, b.Scene.TemporalHistory.Color(b.Scene.TemporalHistory.WriteIndex));
                    float[] sa = TemporalTextureIo.Read(a.Device, a.Scene.TemporalHistory.Confidence(a.Scene.TemporalHistory.WriteIndex));
                    float[] sb = TemporalTextureIo.Read(b.Device, b.Scene.TemporalHistory.Confidence(b.Scene.TemporalHistory.WriteIndex));
                    for (int i = 0; i < ca.Length; i++)
                    {
                        if (BitConverter.SingleToInt32Bits(ca[i]) == BitConverter.SingleToInt32Bits(cb[i])) continue;
                        colourValues++;
                        colourSteps = Math.Max(colourSteps, Steps(ca[i], cb[i]));
                        if (firstFrame < 0) firstFrame = n;
                    }
                    for (int i = 0; i < sa.Length; i++)
                    {
                        if (BitConverter.SingleToInt32Bits(sa[i]) == BitConverter.SingleToInt32Bits(sb[i])) continue;
                        stateValues++;
                        stateSteps = Math.Max(stateSteps, Steps(sa[i], sb[i]));
                        if (firstFrame < 0) firstFrame = n;
                    }
                }
                output.WriteLine($"CROSS {entryName} shipped against {variant} {preset}: colour {colourValues} values "
                    + $"(worst {colourSteps} steps), state {stateValues} values (worst {stateSteps} steps), first frame "
                    + $"{firstFrame}, {rewritten} programs rewritten");
            }
        }

        static Action<Scene3D, int> Draw(PerspectiveFollowLines walk) =>
            (s, n) => walk.Draw(s, Math.Min(n, TemporalFollowLinesRuns.Last), true, true);

        static int Steps(float a, float b) => Math.Abs(Ordinal(a) - Ordinal(b));

        static int Ordinal(float v)
        {
            short bits = BitConverter.HalfToInt16Bits((Half)v);
            return bits < 0 ? -(bits & 0x7fff) : bits;
        }

        public static TheoryData<string> Variants =>
            new() { "inline-gather", "int-rtne", "int-rtne-ftz", "precise-prep", "int-rtne-precise" };

        [GpuTheory]
        [MemberData(nameof(Variants))]
        public void Probe(string variant)
        {
            List<(string Old, string New)> edits = Edits(variant);
            var applied = new bool[edits.Count];
            ShaderSources.TemporalRewriteForTests = glsl =>
            {
                for (int i = 0; i < edits.Count; i++)
                {
                    if (!glsl.Contains(edits[i].Old, StringComparison.Ordinal)) continue;
                    glsl = glsl.Replace(edits[i].Old, edits[i].New, StringComparison.Ordinal);
                    applied[i] = true;
                }
                return glsl;
            };
            try
            {
                foreach (TemporalUpscale preset in new[]
                    { TemporalUpscale.Native, TemporalUpscale.Quality, TemporalUpscale.Performance })
                {
                    output.WriteLine($"PROBE {variant} {preset}");
                    try
                    {
                        new TemporalEntryIdentityGpuTests(output).Both_entry_points_write_the_same_history_on_every_frame(
                            preset);
                        output.WriteLine($"PROBE {variant} {preset}: IDENTICAL");
                    }
                    catch (Exception e)
                    {
                        output.WriteLine($"PROBE {variant} {preset}: DIFFERS {e.Message.Split('\n')[0]}");
                    }
                }
            }
            finally
            {
                ShaderSources.TemporalRewriteForTests = null;
            }
            output.WriteLine($"PROBE {variant} edits applied: {string.Join(",", applied)}");
            Assert.All(applied, Assert.True);
        }
    }
}
