using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The temporal resolve's GLSL, read as text: its tuning constants agree with the C# mirror, its bindings match the
    /// layout Task E6 builds, it reads without gradients, it exposes the pixel function group F re-evaluates, and it keeps
    /// the fullscreen interpolant live for the Direct3D 11 input signature.
    /// </summary>
    public sealed class TemporalResolveShaderTests
    {
        public static TheoryData<string, float, float> Constants => new()
        {
            { "const float MotionSentinel = 60000.0;", TemporalResolveTuning.MotionSentinel, 60000f },
            { "const float BackgroundLinearDepth = 1.0e30;", TemporalResolveTuning.BackgroundLinearDepth, 1.0e30f },
            { "const float DisocclusionTolerance = 0.02;", TemporalResolveTuning.DisocclusionTolerance, 0.02f },
            { "const float MovingSurfaceInternalPixels = 0.5;", TemporalResolveTuning.MovingSurfaceInternalPixels, 0.5f },
            { "const float MovingSurfaceMotionFraction = 0.0009765625;", TemporalResolveTuning.MovingSurfaceMotionFraction, 1f / 1024f },
            { "const float MaxAccumulation = 15.0;", TemporalResolveTuning.MaxAccumulation, 15f },
            { "const float MovingAccumulation = 8.0;", TemporalResolveTuning.MovingAccumulation, 8f },
            { "const float MotionAccumulationPixels = 16.0;", TemporalResolveTuning.MotionAccumulationPixels, 16f },
            { "const float GammaStill = 1.5;", TemporalResolveTuning.GammaStill, 1.5f },
            { "const float GammaMoving = 0.75;", TemporalResolveTuning.GammaMoving, 0.75f },
            { "const float GammaMotionPixels = 3.0;", TemporalResolveTuning.GammaMotionPixels, 3f },
            { "const float ReactiveGain = 4.0;", TemporalResolveTuning.ReactiveGain, 4f },
            { "const float ReactiveStrength = 0.9;", TemporalResolveTuning.ReactiveStrength, 0.9f },
            { "const float LockRidgeAbsolute = 0.02;", TemporalResolveTuning.LockRidgeAbsolute, 0.02f },
            { "const float LockRidgeRelative = 0.1;", TemporalResolveTuning.LockRidgeRelative, 0.1f },
            { "const float LockMotionStartPixels = 1.0;", TemporalResolveTuning.LockMotionStartPixels, 1f },
            { "const float LockMotionEndPixels = 4.0;", TemporalResolveTuning.LockMotionEndPixels, 4f },
            { "const float LockReactiveRelease = 2.0;", TemporalResolveTuning.LockReactiveRelease, 2f },
            { "const float LockDecay = 0.125;", TemporalResolveTuning.LockDecay, 0.125f },
            { "const float LockHoldGain = 2.0;", TemporalResolveTuning.LockHoldGain, 2f },
            { "const float LockEdgeRelease = 1.0;", TemporalResolveTuning.LockEdgeRelease, 1f },
        };

        [Theory]
        [MemberData(nameof(Constants))]
        public void Every_tuning_constant_is_the_same_in_the_shader_and_in_CSharp(string glslLine, float csharp, float expected)
        {
            Assert.Contains(glslLine, ShaderSources.TemporalResolveCoreGlsl, StringComparison.Ordinal);
            Assert.Equal(expected, csharp);
        }

        [Fact]
        public void The_core_declares_the_nine_bindings_in_layout_order_and_no_stage_inputs_or_outputs()
        {
            string[] bindings =
            {
                "layout(set=0, binding=0) uniform texture2D SceneColor;",
                "layout(set=0, binding=1) uniform texture2D OpaqueColor;",
                "layout(set=0, binding=2) uniform texture2D SceneDepth;",
                "layout(set=0, binding=3) uniform texture2D MotionTex;",
                "layout(set=0, binding=4) uniform texture2D PrevDepth;",
                "layout(set=0, binding=5) uniform texture2D HistoryColor;",
                "layout(set=0, binding=6) uniform texture2D HistoryConfidence;",
                "layout(set=0, binding=7) uniform sampler LinearClamp;",
                "layout(set=0, binding=8) uniform Resolve {",
            };
            int last = -1;
            foreach (string binding in bindings)
            {
                int at = ShaderSources.TemporalResolveCoreGlsl.IndexOf(binding, StringComparison.Ordinal);
                Assert.True(at > last, $"{binding} is missing or out of layout order");
                last = at;
            }
            Assert.DoesNotContain("layout(location", ShaderSources.TemporalResolveCoreGlsl, StringComparison.Ordinal);
            Assert.DoesNotContain("#version", ShaderSources.TemporalResolveCoreGlsl, StringComparison.Ordinal);
        }

        [Fact]
        public void Both_uniform_blocks_declare_exactly_the_members_of_their_CSharp_structs()
        {
            Assert.Contains("layout(set=0, binding=8) uniform Resolve {" + TemporalResolveUniforms.GlslMembers + "};",
                ShaderSources.TemporalResolveCoreGlsl, StringComparison.Ordinal);
            Assert.Contains("layout(set=0, binding=3) uniform DepthStore {" + TemporalDepthStoreUniforms.GlslMembers + "};",
                ShaderSources.TemporalDepthStoreFrag, StringComparison.Ordinal);
        }

        [Fact]
        public void The_expected_depth_and_the_static_previous_position_are_the_CSharp_mirrors_in_GLSL()
        {
            // TemporalResolveMath.ExpectedPreviousDepth and StaticPreviousUv, term for term, at the dilated texel's own
            // unjittered sample position and depth, which is the point that texel's motion was written for.
            string core = ShaderSources.TemporalResolveCoreGlsl;
            Assert.Contains("vec2 samplePosition = vec2(texel) + 0.5 - jitter;", core, StringComparison.Ordinal);
            Assert.Contains("closestSample = samplePosition;", core, StringComparison.Ordinal);
            Assert.Contains("vec2 closestUv = closestSample / internalSize;", core, StringComparison.Ordinal);
            Assert.Contains("vec2 closestNdc = vec2(closestUv.x * 2.0 - 1.0, 1.0 - closestUv.y * 2.0);", core,
                StringComparison.Ordinal);
            Assert.Contains("float clipW = CurrentDepth.x > 0.5 ? closestDepth : 1.0;", core, StringComparison.Ordinal);
            Assert.Contains("vec4 previousView = CurrentToPrevious * vec4(closestNdc * clipW, closestDepth, 1.0);", core,
                StringComparison.Ordinal);
            Assert.Contains("expectedDepth = -previousView.z;", core, StringComparison.Ordinal);
            Assert.Contains("vec4 staticClip = PreviousProjection * previousView;", core, StringComparison.Ordinal);
            Assert.Contains("if (staticClip.w > 1.0e-6) {", core, StringComparison.Ordinal);
            Assert.Contains("vec2 staticUv = vec2(staticClip.x / staticClip.w * 0.5 + 0.5, 0.5 - staticClip.y / staticClip.w * 0.5);",
                core, StringComparison.Ordinal);
            Assert.Contains("vec2 surfaceMotion = (staticUv - (closestUv - closestMotion)) * internalSize;", core,
                StringComparison.Ordinal);
            Assert.Contains("float movingThreshold = MovingSurfaceInternalPixels + length(closestMotion * internalSize) "
                + "* MovingSurfaceMotionFraction;", core, StringComparison.Ordinal);
            Assert.Contains("depthTested = length(surfaceMotion) <= movingThreshold;", core, StringComparison.Ordinal);
            Assert.DoesNotContain("CurrentToPrevious * vec4(ndcXY", core, StringComparison.Ordinal);

            // History is still read where the dilated motion carries the display pixel.
            Assert.Contains("previousUv = uv - closestMotion;", core, StringComparison.Ordinal);
            Assert.Contains("bool isBackground = abs(motion.x) > MotionSentinel;", core, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_declared_resource_survives_the_optimised_compile_in_binding_order()
        {
            const GpuResourceKind T = GpuResourceKind.TextureReadOnly;
            AssertSurvives(ShaderSources.TemporalResolveFrag, "TemporalResolve",
                new[] { "t0", "t1", "t2", "t3", "t4", "t5", "t6", "s0", "b0" },
                T, T, T, T, T, T, T, GpuResourceKind.Sampler, GpuResourceKind.UniformBuffer);
            AssertSurvives(ShaderSources.TemporalDepthStoreFrag, "TemporalDepthStore", new[] { "t0", "t1", "s0", "b0" },
                T, T, GpuResourceKind.Sampler, GpuResourceKind.UniformBuffer);
        }

        [Fact]
        public void The_history_fetch_is_clamped_to_its_bilinear_texel_footprint_before_the_finite_check()
        {
            // The outer Catmull-Rom weights go negative, so a bright history texel beside a dark one would ring below
            // zero, and the clamp to zero after it would leave a black halo inside the variance box. The bound is the
            // four history texels around the position, not the five taps, whose bilinear blends sit below a thin line's
            // peak and flattened it on every fractional resample.
            string core = ShaderSources.TemporalResolveCoreGlsl;
            Assert.Contains("ivec2 base = ivec2(floor(samplePos - 0.5));", core, StringComparison.Ordinal);
            foreach (string offset in new[] { "base", "base + ivec2(1, 0)", "base + ivec2(0, 1)", "base + ivec2(1, 1)" })
                Assert.Contains($"texelFetch(sampler2D(HistoryColor, LinearClamp), clamp({offset}, ivec2(0), lastTexel), 0);",
                    core, StringComparison.Ordinal);
            Assert.Contains("vec4 texelMin = min(min(t00, t10), min(t01, t11));", core, StringComparison.Ordinal);
            Assert.Contains("vec4 texelMax = max(max(t00, t10), max(t01, t11));", core, StringComparison.Ordinal);
            Assert.Contains("return clamp(sum / weight, texelMin, texelMax);", core, StringComparison.Ordinal);
            Assert.DoesNotContain("tapMax", core, StringComparison.Ordinal);
            int fetch = core.IndexOf("vec4 fetched = sampleHistoryCatmullRom(previousUv, displaySize);", StringComparison.Ordinal);
            int finite = core.IndexOf("bool finite = ", StringComparison.Ordinal);
            Assert.True(fetch >= 0 && finite > fetch, "the clamped fetch must come before the finite check");
        }

        [Fact]
        public void The_thin_feature_lock_decays_the_same_at_every_preset_follows_its_history_and_releases_at_moving_edges()
        {
            // A lock that decayed by one over the jitter cycle, 72 frames at UltraPerformance, held a moving line's
            // trail, so the decay is a constant and Params.x is reserved. The lock is the largest of the state texels
            // that carry bilinear weight, because a bilinear lock under motion ran out early. The release is the centre
            // texel's motion against the dilated one, which is zero for a surface moving as a whole, not the surface's
            // own travel, which released a swaying blade and an avatar the camera follows.
            string core = ShaderSources.TemporalResolveCoreGlsl;
            Assert.Contains("float lockValue = useHistory ? max(historyState.y - LockDecay, 0.0) : 0.0;", core,
                StringComparison.Ordinal);
            Assert.DoesNotMatch(@"\bParams\.x", core);
            Assert.Contains("vec4 carried = step(vec4(1.0e-3), bilinear);", core, StringComparison.Ordinal);
            Assert.Contains("max(max(carried.x * s00.y, carried.y * s10.y), max(carried.z * s01.y, carried.w * s11.y)));", core,
                StringComparison.Ordinal);
            Assert.DoesNotContain("textureLod(sampler2D(HistoryConfidence", core, StringComparison.Ordinal);
            Assert.Contains("edgeMotion = length((closestMotion - centreOwn) * internalSize);", core, StringComparison.Ordinal);
            Assert.Contains("* (1.0 - clamp(edgeMotion * LockEdgeRelease, 0.0, 1.0));", core, StringComparison.Ordinal);
            Assert.DoesNotContain("surfaceTravel", core, StringComparison.Ordinal);
            Assert.Contains("float hold = clamp(lockValue * LockHoldGain, 0.0, 1.0);", core, StringComparison.Ordinal);
            Assert.Contains("excess.x *= 1.0 - hold;", core, StringComparison.Ordinal);
            Assert.Contains("clipped.x = mix(clipped.x, historyYcc.x, hold);", core, StringComparison.Ordinal);
        }

        [Fact]
        public void An_infinite_scene_or_opaque_colour_is_held_finite_before_the_luma_weighting()
        {
            string core = ShaderSources.TemporalResolveCoreGlsl;
            Assert.Contains("const float HalfMax = 65504.0;", core, StringComparison.Ordinal);
            Assert.Contains("toWeighted(min(max(sceneColor.rgb, vec3(0.0)), vec3(HalfMax)))", core, StringComparison.Ordinal);
            Assert.Contains("toWeighted(min(max(opaqueColor, vec3(0.0)), vec3(HalfMax)))", core, StringComparison.Ordinal);
            Assert.DoesNotContain("toWeighted(max(", core, StringComparison.Ordinal);
        }

        [Fact]
        public void The_separable_Lanczos_kernel_takes_twelve_evaluations_per_pixel()
        {
            // One definition and four calls in a three-pass loop: three weights per axis at internal scale and three per
            // axis at display scale, formed into the nine products of the 3x3.
            string core = ShaderSources.TemporalResolveCoreGlsl;
            Assert.Equal(5, core.Split("lanczos2(").Length - 1);
            Assert.Contains("for (int i = 0; i < 3; i++) {", core, StringComparison.Ordinal);
            Assert.Contains("float lanczosWeight = kernelX[x + 1] * kernelY[y + 1];", core, StringComparison.Ordinal);
            Assert.Contains("clamp(displayKernelX[x + 1] * displayKernelY[y + 1], 0.0, 1.0)", core, StringComparison.Ordinal);
        }

        [Fact]
        public void Both_programs_read_their_inputs_without_gradients()
        {
            Assert.DoesNotContain("texture(sampler2D", ShaderSources.TemporalResolveCoreGlsl, StringComparison.Ordinal);
            Assert.DoesNotContain("texture(sampler2D", ShaderSources.TemporalDepthStoreFrag, StringComparison.Ordinal);
        }

        [Fact]
        public void The_core_exposes_the_pixel_function_group_F_re_evaluates()
        {
            string core = ShaderSources.TemporalResolveCoreGlsl;
            Assert.Contains("struct TemporalPixel { vec3 color; float confidence; float stability; float disocclusion; "
                + "float reactive; float clip; float alpha; };", core, StringComparison.Ordinal);
            Assert.Contains("TemporalPixel temporalResolvePixel(ivec2 displayPixel) {", core, StringComparison.Ordinal);
            Assert.Contains("ivec2 temporalDisplaySize() {", core, StringComparison.Ordinal);
            Assert.Contains("vec2 uv = (vec2(displayPixel) + 0.5) / displaySize;", core, StringComparison.Ordinal);
        }

        [Fact]
        public void The_resolve_is_the_core_plus_a_main_that_writes_colour_and_state()
        {
            string frag = ShaderSources.TemporalResolveFrag;
            Assert.StartsWith("#version 450\n" + ShaderSources.TemporalResolveCoreGlsl, frag, StringComparison.Ordinal);
            Assert.Contains("layout(location=0) out vec4 oColor;", frag, StringComparison.Ordinal);
            Assert.Contains("layout(location=1) out vec4 oState;", frag, StringComparison.Ordinal);
            Assert.Contains("temporalResolvePixel(ivec2(gl_FragCoord.xy))", frag, StringComparison.Ordinal);
        }

        [Fact]
        public void Both_programs_keep_the_fullscreen_interpolant_live()
        {
            foreach (string frag in new[] { ShaderSources.TemporalResolveFrag, ShaderSources.TemporalDepthStoreFrag })
            {
                Assert.Contains("layout(location=0) in vec2 vUv;", frag, StringComparison.Ordinal);
                Assert.Contains("vUv.x * 1.0e-30", frag, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void The_depth_store_shares_the_background_sentinel_and_the_linearisation()
        {
            Assert.Contains(ShaderSources.TemporalCommonGlsl, ShaderSources.TemporalDepthStoreFrag, StringComparison.Ordinal);
            Assert.Contains(ShaderSources.TemporalCommonGlsl, ShaderSources.TemporalResolveCoreGlsl, StringComparison.Ordinal);
            Assert.StartsWith("#version 450", ShaderSources.TemporalDepthStoreFrag, StringComparison.Ordinal);
        }

        // The optimiser strips a resource the program never reads, and the reflected layout and the Direct3D 11
        // registers are numbered over what survives. So the layout the renderer declares is only right when every
        // binding here is live after the Performance-level compile. The compile also strips names, so each binding is
        // found by its SPIR-V decorations, which survive, and joined on the variable id the emitted HLSL names it by
        // (_<id>) to the register that HLSL gives it. FullscreenVert declares no resources, so the fragment module holds
        // them all. The reflected layout's kinds are checked in its own order too.
        static void AssertSurvives(string fragment, string label, string[] registerByBinding, params GpuResourceKind[] kinds)
        {
            CrossCompiledPair pair = SpirvCrossCompile.GlslPairToHlsl(ShaderSources.FullscreenVert, fragment, label);
            GpuResourceLayoutDescription layout = Assert.Single(pair.Reflection.ResourceLayouts);
            Assert.Equal(kinds, layout.Elements.Select(e => e.Kind).ToArray());

            byte[] spirv = SpirvFrontEnd.ToSpirv(fragment, GpuShaderStages.Fragment, label);
            SpirvResourceDecoration[] declared = SpirvResourceDecorations.Read(spirv, label).Values
                .OrderBy(d => d.Binding).ToArray();
            Assert.All(declared, d => Assert.Equal(0u, d.Set));
            Assert.Equal(Enumerable.Range(0, registerByBinding.Length).Select(b => (uint)b), declared.Select(d => d.Binding));

            var registerById = new Dictionary<uint, string>();
            foreach (Match m in Regex.Matches(pair.FragmentSource, @"_(\d+) : register\(([btsu]\d+)\)"))
                registerById[uint.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = m.Groups[2].Value;
            Assert.Equal(registerByBinding, declared.Select(d => registerById[d.Id]).ToArray());
        }
    }
}
