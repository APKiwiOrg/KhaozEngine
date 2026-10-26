using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Runs the shipped entry filter over an occupied atlas, including near-horizontal fit coefficients.</summary>
public sealed class UnderwaterEntryKernelGpuTests(ITestOutputHelper output)
{
    const string Fragment = @"#version 450
layout(set=0, binding=0) uniform texture2D ShadowMap;
layout(set=0, binding=1) uniform sampler ShadowSamp;
layout(set=0, binding=2) uniform Cases { vec4 Items[384]; };
layout(location=0) out vec4 oColor;
mat4 ShadowMat[4];
vec4 ShadowParams;
vec4 ShadowParams2;
struct UnderwaterKey { float pathLength; float penumbraPerMetre; };
" + ShaderSources.CascadeShadowFilterGlsl + ShaderSources.UnderwaterShadowKernelGlsl + @"
void main() {
    int i = int(gl_FragCoord.x) * 6;
    ShadowMat[0] = mat4(Items[i], Items[i + 1], Items[i + 2], Items[i + 3]);
    ShadowParams = vec4(1.0, 1.0, 0.0, 0.0);
    ShadowParams2 = Items[i + 5];
    vec4 p = Items[i + 4];
    float lit = filterKeyShadowWidened(p.xy, p.z, 0, p.w, vec2(0.0));
    uint bits = floatBitsToUint(lit);
    oColor = vec4(float(bits & 255u), float((bits >> 8) & 255u),
                  float((bits >> 16) & 255u), float(bits >> 24)) / 255.0;
}
";

    [GpuFact]
    public void WidenedEntryMatchesDoubleReferenceAtEveryElevation()
    {
        var samples = UnderwaterEntryKernelOracle.Elevations;
        var items = new Vector4[384];
        for (int sample = 0; sample < samples.Length; sample++)
            for (int variant = 0; variant < 2; variant++)
            {
                Matrix4x4 m = UnderwaterEntryKernelOracle.Matrix(samples[sample].Radians, samples[sample].Radius);
                int i = (sample * 2 + variant) * 6;
                items[i] = new Vector4(m.M11, m.M12, m.M13, m.M14);
                items[i + 1] = new Vector4(m.M21, m.M22, m.M23, m.M24);
                items[i + 2] = new Vector4(m.M31, m.M32, m.M33, m.M34);
                items[i + 3] = new Vector4(m.M41, m.M42, m.M43, m.M44);
                items[i + 4] = new Vector4(0.5f, 0.5f, 0.5f, variant == 0 ? 1f : 8f);
                items[i + 5] = new Vector4(1f / 32f, 0f, 0f, 0f);
            }
        var atlas = new float[32 * 32];
        Array.Fill(atlas, 0.75f);
        using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
        byte[] rgba = TemporalShadowFilterGpuTests.Render(gpu.GpuDevice, Fragment, atlas, 32, 32,
            items, samples.Length * 2);
        var problems = new List<string>();
        for (int sample = 0; sample < samples.Length; sample++)
        {
            Matrix4x4 matrix = UnderwaterEntryKernelOracle.Matrix(samples[sample].Radians, samples[sample].Radius);
            float expected = 0f;
            int count = 0;
            foreach (var tap in UnderwaterEntryKernelOracle.Taps())
            {
                if (count++ == 16) break;
                if (UnderwaterEntryKernelOracle.Compare(matrix, tap.Wide - tap.Dry, 0.75f, 0.5f).Lit)
                    expected += 1f / 16f;
            }
            float dry = Read(sample * 2), wide = Read(sample * 2 + 1);
            output.WriteLine($"elevation={samples[sample].Radians:E6} rad, fit radius={samples[sample].Radius}, dry={dry}, widened={wide}, double reference={expected}");
            if (dry != 1f || !float.IsFinite(wide) || MathF.Abs(wide - expected) > 1e-6f)
                problems.Add($"Elevation {samples[sample].Radians} rad, radius {samples[sample].Radius}: dry {dry}, widened {wide}, expected {expected}.");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));

        float Read(int pixel) => BitConverter.Int32BitsToSingle(rgba[pixel * 4] | rgba[pixel * 4 + 1] << 8
            | rgba[pixel * 4 + 2] << 16 | rgba[pixel * 4 + 3] << 24);
    }
}
