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

        const string IntHalfFunction = @"float probeHalf(float v) {
    uint bits = floatBitsToUint(v);
    uint normal = (bits + 0xfffu + ((bits >> 13) & 1u)) & 0xffffe000u;
    float subnormal = roundEven(v * 16777216.0) * 5.9604644775390625e-8;
    return mix(uintBitsToFloat(normal), subnormal, (bits & 0x7fffffffu) < 0x38800000u);
}
";

        static List<(string Old, string New)> Edits(string variant) => variant switch
        {
            // The split's first pass rounds in integer steps, the fused pass keeps packHalf2x16 and unpackHalf2x16.
            "int-first-pass" =>
            [
                ("layout(location=0) out vec4 oPrepared;", IntHalfFunction + "layout(location=0) out vec4 oPrepared;"),
                ("    oPrepared = temporalExactHalf(temporalPrepared(ycc, reactiveDifference));",
                    "    oPrepared = vec4(probeHalf(ycc.x), probeHalf(ycc.y), probeHalf(ycc.z), "
                    + "probeHalf(reactiveDifference));"),
            ],
            // Both entries round in integer steps, without branches.
            "int-both" =>
            [
                (Prepared, IntHalfFunction + @"vec4 temporalPrepared(vec3 ycc, float reactiveDifference) {
    return vec4(probeHalf(ycc.x), probeHalf(ycc.y), probeHalf(ycc.z), probeHalf(reactiveDifference));
}"),
            ],
            _ => throw new ArgumentException(variant),
        };

        public static TheoryData<string> Variants => new() { "int-first-pass", "int-both" };

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
