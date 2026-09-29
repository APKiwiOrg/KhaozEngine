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
    /// layout <c>TemporalResolveRenderer</c> builds, it reads without gradients, it exposes the pixel function the
    /// temporal debug views and sampled counts re-evaluate, and it keeps the fullscreen interpolant live for the
    /// Direct3D 11 input signature.
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
            { "const float LockEdgeMotionFraction = 0.001953125;", TemporalResolveTuning.LockEdgeMotionFraction, 1f / 512f },
            { "const float LockEdgeFloorInternalPixels = 0.001;", TemporalResolveTuning.LockEdgeFloorInternalPixels, 1e-3f },
            { "const float ClipFlagMinimumMove = 0.0009765625;", TemporalResolveTuning.ClipFlagMinimumMove, 1f / 1024f },
            {
                "const float DilationReachInternalPixels = 1.25;", TemporalResolveTuning.DilationReachInternalPixels,
                1.25f
            },
            { "const float WorldMotionMetres = 0.001;", TemporalResolveTuning.WorldMotionMetres, 0.001f },
            { "const float FollowedTravelRatio = 2.0;", TemporalResolveTuning.FollowedTravelRatio, 2f },
            {
                "const float FollowedHistoryMotionFraction = 0.5;",
                TemporalResolveTuning.FollowedHistoryMotionFraction, 0.5f
            },
            {
                "const float FollowedStillDisplayPixels = 0.1;", TemporalResolveTuning.FollowedStillDisplayPixels, 0.1f
            },
            { "const float DisocclusionVisibleShare = 0.5;", TemporalResolveTuning.DisocclusionVisibleShare, 0.5f },
            { "const float MovingShareConfidence = 0.0;", TemporalResolveTuning.MovingShareConfidence, 0f },
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
            // TemporalResolveMath.ExpectedPreviousDepth and StaticPreviousUv, term for term, at a texel's own
            // unjittered sample position and depth, which is the point that texel's motion was written for.
            string core = ShaderSources.TemporalResolveCoreGlsl;
            Assert.Contains("closestSample = vec2(closestTexel) + 0.5 - jitter;", core, StringComparison.Ordinal);
            Assert.Contains("closestTexel = texel;", core, StringComparison.Ordinal);
            Assert.Contains("vec2 sampleUv = sampleInternal / internalSize;", core, StringComparison.Ordinal);
            Assert.Contains("vec2 sampleNdc = vec2(sampleUv.x * 2.0 - 1.0, 1.0 - sampleUv.y * 2.0);", core,
                StringComparison.Ordinal);
            Assert.Contains("float clipW = CurrentDepth.x > 0.5 ? depth : 1.0;", core, StringComparison.Ordinal);
            Assert.Contains("vec4 previousView = CurrentToPrevious * vec4(sampleNdc * clipW, depth, 1.0);", core,
                StringComparison.Ordinal);
            Assert.Contains("expectedDepth = -previousView.z;", core, StringComparison.Ordinal);
            Assert.Contains("vec4 staticClip = PreviousProjection * previousView;", core, StringComparison.Ordinal);
            Assert.Contains("if (staticClip.w > 1.0e-6) {", core, StringComparison.Ordinal);
            Assert.Contains("vec2 staticUv = vec2(staticClip.x / staticClip.w * 0.5 + 0.5, 0.5 - staticClip.y / staticClip.w * 0.5);",
                core, StringComparison.Ordinal);
            Assert.Contains("vec2 surfaceMotion = (staticUv - (sampleUv - motion)) * internalSize;", core,
                StringComparison.Ordinal);
            Assert.Contains("float movingThreshold = MovingSurfaceInternalPixels", core, StringComparison.Ordinal);
            Assert.Contains("+ length(motion * internalSize) * MovingSurfaceMotionFraction;", core,
                StringComparison.Ordinal);
            Assert.Contains("travel = length(surfaceMotion);", core, StringComparison.Ordinal);
            Assert.Contains("depthTested = travel <= movingThreshold;", core, StringComparison.Ordinal);
            Assert.DoesNotContain("CurrentToPrevious * vec4(ndcXY", core, StringComparison.Ordinal);

            // History is read where the dilated motion carries the display pixel, from the dilated texel's own point,
            // which the centre texel's surface holds unless the pixel reprojects by its own.
            Assert.Contains("previousUv = uv - motion;", core, StringComparison.Ordinal);
            Assert.Contains("temporalReprojectSurface(closestSample, closestMotion, closestDepth, closestIsBackground, "
                + "internalSize,", core, StringComparison.Ordinal);
            Assert.Contains("surface.motion = closestMotion;", core, StringComparison.Ordinal);
            Assert.Contains("vec2 previousUv = temporalPreviousUv(uv, surface.motion, surface.background);", core,
                StringComparison.Ordinal);
            Assert.Contains("closestMotion = texelFetch(sampler2D(MotionTex, LinearClamp), closestTexel, 0).rg;", core,
                StringComparison.Ordinal);
            Assert.Contains("closestIsBackground = abs(closestMotion.x) > MotionSentinel;", core,
                StringComparison.Ordinal);
        }

        [Fact]
        public void Beyond_the_dilation_reach_a_pixel_on_the_farther_surface_reprojects_by_its_own_centre_texel()
        {
            // Dilation reads history along the nearer surface's motion. Where that surface moved, the centre texel
            // lies on a farther surface and the two move more than DilationReachInternalPixels apart, that history is
            // another texel of the farther surface, so the pixel reprojects by its centre texel's own motion and depth,
            // after the edge signal and before the depth test that reads what it reprojected. A still nearer surface,
            // under a camera's translation or against the sky, keeps dilation.
            string core = ShaderSources.TemporalResolveCoreGlsl;
            Assert.Contains("float centreDepth = temporalViewDepth(centreMotion,", core, StringComparison.Ordinal);
            Assert.Contains("vec2 centreSample = vec2(centreTexel) + 0.5 - jitter;", core, StringComparison.Ordinal);
            string own = "bool ownReprojected = !depthTested && edgeMotion > DilationReachInternalPixels";
            Assert.Contains(own, core, StringComparison.Ordinal);
            Assert.Contains("bool centreFarther = centreDepth > closestDepth * (1.0 + DisocclusionTolerance);", core,
                StringComparison.Ordinal);
            Assert.Contains(own + " && centreFarther;", core, StringComparison.Ordinal);
            Assert.Contains("if (ownReprojected) {", core, StringComparison.Ordinal);
            Assert.Contains("temporalReprojectSurface(centreSample, centreMotion, centreDepth, centreIsBackground, "
                + "internalSize,", core, StringComparison.Ordinal);
            Assert.Contains("surface.motion = centreMotion;", core, StringComparison.Ordinal);
            int edge = core.IndexOf("bool movingEdge = ", StringComparison.Ordinal);
            int reach = core.IndexOf(own, StringComparison.Ordinal);
            int test = core.IndexOf("DepthFootprint footprint = temporalDepthFootprint(previousUv,",
                StringComparison.Ordinal);
            Assert.True(edge >= 0 && reach > edge && test > reach,
                "the own reprojection must follow the edge signal it reads and precede the depth test");
        }

        [Fact]
        public void Beside_a_fast_narrow_feature_the_own_history_takes_the_feature_s_current_colour_for_its_share()
        {
            // Where the pixel reprojected by its own motion and the nearer surface is narrow in this frame's depth,
            // the share of the reconstruction weight on texels nearer than the centre texel's own surface takes their
            // current colour in the clipped history, before the blend, save the share the pixel's lock holds, and the
            // pixel stores MovingShareConfidence. A pixel whose hold is whole keeps its own history and confidence. The
            // nearer texels are summed inside that branch alone, so the 3x3 every pixel runs carries no sum for them.
            string core = ShaderSources.TemporalResolveCoreGlsl;
            Assert.Contains("float ownDepth = temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), "
                + "centreTexel, 0).rg,", core, StringComparison.Ordinal);
            Assert.Contains("if (ownDepth > viewDepth * (1.0 + DisocclusionTolerance)) {", core,
                StringComparison.Ordinal);
            Assert.Contains("nearerSum += vec4(ycc, sceneColor.a) * lanczosWeight;", core, StringComparison.Ordinal);
            Assert.Contains("closestTexel = texel;", core, StringComparison.Ordinal);
            Assert.Contains("bool nearerNarrow = (ownReprojected || band) && temporalNarrowDepth(closestTexel, "
                + "closestDepth, maxTexel, true);", core, StringComparison.Ordinal);
            Assert.Contains("bool narrowMoving = ownReprojected && nearerNarrow;", core, StringComparison.Ordinal);
            Assert.Contains("bool temporalNarrowDepth(ivec2 texel, float depth, ivec2 maxTexel, bool current) {", core,
                StringComparison.Ordinal);
            Assert.DoesNotContain("temporalNarrowCurrentDepth", core, StringComparison.Ordinal);
            Assert.Contains("if (nearerWeight > 1.0e-4) {", core, StringComparison.Ordinal);
            Assert.Contains("movingShare = clamp(nearerWeight / max(reconstructionWeight, 1.0e-4), 0.0, 1.0) * (1.0 - "
                + "hold);", core, StringComparison.Ordinal);
            Assert.Contains("clipped = mix(clipped, clamp(movingColor.xyz, neighbourMin, neighbourMax), movingShare);",
                core, StringComparison.Ordinal);
            Assert.Contains("result.confidence = movingShare > 0.0 ? MovingShareConfidence", core,
                StringComparison.Ordinal);
            int reach = core.IndexOf("bool ownReprojected = !depthTested && edgeMotion > DilationReachInternalPixels",
                StringComparison.Ordinal);
            int narrow = core.IndexOf("bool narrowMoving = ownReprojected && nearerNarrow;", StringComparison.Ordinal);
            int hold = core.IndexOf("clipped.x = mix(clipped.x, historyYcc.x, hold);", StringComparison.Ordinal);
            int share = core.IndexOf("if (narrowMoving) {", StringComparison.Ordinal);
            int own = core.IndexOf("float ownDepth = ", StringComparison.Ordinal);
            int sum = core.IndexOf("nearerSum += ", StringComparison.Ordinal);
            int blend = core.IndexOf("vec3 resolvedYcc = mix(clipped, current.xyz, currentWeight);",
                StringComparison.Ordinal);
            Assert.True(reach >= 0 && narrow > reach && hold > narrow && share > hold && blend > share,
                "the narrow test follows rule 1, and the colour replaces the clipped history before the blend");
            Assert.True(own > share && sum > own && blend > sum,
                "the nearer texels are summed inside the narrow moving branch, not in the 3x3 every pixel runs");
        }

        [Fact]
        public void A_mostly_covered_footprint_is_disoccluded_unless_a_still_narrow_feature_or_a_fresh_lock_covered_it()
        {
            // All four stored depths nearer than expected, or the expected surface under less than
            // DisocclusionVisibleShare of their bilinear weight while the lock the pixel carries lies more than half of
            // LockDecay below whole and the nearest of them is no narrow feature, a run of at most two texels along a
            // row or a column, where the state says no moving surface showed there last frame. The lock and that fact
            // are read from the state texels step 2 reads, fetched once before the test. The stored lock is minus one
            // minus the lock where the surface the pixel reprojected by moved, minus three minus the lock where the
            // pixel followed a nearer surface's edge that moved, minus five minus the lock on a pixel of the followed
            // surface itself, and reads back unchanged. The moved mark is the reprojected surface's own depth test, not
            // the band's world test of the nearer surface.
            // A Windows checkout has CRLF line ends, and a pin below spans two lines of the shader.
            string core = ShaderSources.TemporalResolveCoreGlsl.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Contains("float weight = mix(1.0 - f.x, f.x, float(corner & 1)) "
                + "* mix(1.0 - f.y, f.y, float(corner >> 1));", core, StringComparison.Ordinal);
            Assert.Contains("if (!(stored < expectedDepth * (1.0 - DisocclusionTolerance))) "
                + "footprint.visibleShare += weight;", core, StringComparison.Ordinal);
            Assert.Contains("|| footprint.farthest < expectedDepth * (1.0 - DisocclusionTolerance)", core,
                StringComparison.Ordinal);
            Assert.Contains(": footprint.visibleShare < DisocclusionVisibleShare", core, StringComparison.Ordinal);
            Assert.Contains("&& fetchedState.y <= 1.0 - 0.5 * LockDecay", core, StringComparison.Ordinal);
            Assert.Contains("&& !temporalNarrowDepth(footprint.nearestTexel, footprint.nearest, maxTexel, false)));",
                core, StringComparison.Ordinal);
            Assert.Contains("return moved > 2.5 ? -5.0 - lockValue : moved > 1.5 ? -3.0 - lockValue : moved > 0.5 ? "
                + "-1.0 - lockValue\n        : lockValue;", core, StringComparison.Ordinal);
            Assert.Contains("bool temporalStoredMoved(float stored) { return stored < -0.5; }", core,
                StringComparison.Ordinal);
            Assert.Contains("return temporalStoredMoved(stored) ? mod(-1.0 - stored, 2.0) : stored;", core,
                StringComparison.Ordinal);
            Assert.DoesNotContain("result.moved = historyValid && nearerMoved", core, StringComparison.Ordinal);
            Assert.Contains("result.moved = followed ? 3.0 : band ? 2.0 : historyValid && !depthTested ? 1.0 : 0.0;",
                core, StringComparison.Ordinal);
            foreach (var (texel, weight) in new[] { ("s00", "x"), ("s10", "y"), ("s01", "z"), ("s11", "w") })
            {
                Assert.Contains($"{texel}.y = temporalStoredLock({texel}.y);", core, StringComparison.Ordinal);
                Assert.Contains($"carried.{weight} > 0.5 ? {texel}.y : 0.0", core, StringComparison.Ordinal);
            }
            Assert.Contains("carriedMoved = temporalStoredMoved(carriedLeast);", core, StringComparison.Ordinal);
            Assert.Contains("run[i] = !(depth < there * limit || there < depth * limit);", core,
                StringComparison.Ordinal);
            Assert.Contains("bool narrowRow = !(run[0] && run[2]) && !(run[0] && run[1]) && !(run[2] && run[3]);", core,
                StringComparison.Ordinal);
            Assert.Contains("return narrowRow || narrowColumn;", core, StringComparison.Ordinal);
            Assert.DoesNotContain("temporalCarriedLock", core, StringComparison.Ordinal);
            Assert.Equal(4, core.Split("texelFetch(sampler2D(HistoryConfidence").Length - 1);
            int state = core.IndexOf("vec2 fetchedState = vec2(0.0);", StringComparison.Ordinal);
            int test = core.IndexOf("DepthFootprint footprint = temporalDepthFootprint(previousUv,",
                StringComparison.Ordinal);
            int history = core.IndexOf("vec4 fetched = sampleHistoryCatmullRom(previousUv, displaySize);",
                StringComparison.Ordinal);
            Assert.True(state >= 0 && test > state && history > test,
                "the state must be read once, before the depth test and the history fetch that both use it");
        }

        [Fact]
        public void A_footprint_a_moving_surface_showed_at_keeps_its_history_only_where_it_is_whole()
        {
            // Where the surface the pixel reprojected by moved, the state stores minus one minus the lock, a band pixel
            // minus three and a followed one minus five, which count as moved too. Where any texel carrying weight
            // holds one, neither the narrow nor the lock exception applies, and any weight on a nearer stored depth
            // drops the history. The narrow and lock exceptions keep their rule elsewhere.
            string core = ShaderSources.TemporalResolveCoreGlsl;
            Assert.Contains("out float expectedDepth, out bool depthTested, out float travel) {", core,
                StringComparison.Ordinal);
            Assert.Contains("travel = length(surfaceMotion);", core, StringComparison.Ordinal);
            Assert.Contains("depthTested = travel <= movingThreshold;", core, StringComparison.Ordinal);
            Assert.Contains("travel = 1.0e30;   // beyond every bound, in internal pixels", core,
                StringComparison.Ordinal);
            Assert.Contains("|| (carriedMoved ? footprint.visibleShare < 1.0 - 1.0e-3", core, StringComparison.Ordinal);
            Assert.DoesNotContain("carriedFast", core, StringComparison.Ordinal);
            Assert.DoesNotContain("temporalStoredFast", core, StringComparison.Ordinal);
            Assert.Equal(2, core.Split("expectedDepth, depthTested, travel);").Length - 1);
            int moved = core.IndexOf("carriedMoved = temporalStoredMoved(carriedLeast);", StringComparison.Ordinal);
            int lockRead = core.IndexOf("s00.y = temporalStoredLock(s00.y);", StringComparison.Ordinal);
            int test = core.IndexOf("|| (carriedMoved ? footprint.visibleShare", StringComparison.Ordinal);
            Assert.True(moved >= 0 && lockRead > moved && test > lockRead,
                "the moved flag is read from the stored state before its lock is decoded, and before the depth test");
        }

        [Fact]
        public void A_still_surface_pixel_drops_the_history_the_band_left_beside_or_on_a_followed_surface()
        {
            // The band: a pixel beside or on a wide nearer surface that moved in the world, past WorldMotionMetres at
            // its depth through last frame's projection and the rounding fraction. Beside its edge the farther centre
            // moves more on screen, on it the surface travelled more than twice its screen motion. Beside the edge it
            // stores minus three minus the lock, on the surface minus five minus the lock. A depth-tested pixel whose
            // nearest surface did not move drops a history that carries either, unless every stored depth is farther,
            // or the followed surface's own pixels stored it and the pixel moved on screen no more than
            // FollowedStillDisplayPixels, or none of the nine current texels around the history position, background
            // aside, moves on screen otherwise than the pixel by more than FollowedHistoryMotionFraction of its motion.
            // No 3x3 farthest depth spares it, and the nine texels are read outside the 3x3 every pixel runs.
            // A Windows checkout has CRLF line ends, and a pin below spans two lines of the shader.
            string core = ShaderSources.TemporalResolveCoreGlsl.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Contains("bool temporalStoredBand(float stored) { return stored < -2.5; }", core,
                StringComparison.Ordinal);
            Assert.Contains("bool nearerMoved = !depthTested || travel > WorldMotionMetres * PreviousProjection[0][0] "
                + "* internalSize.x * 0.5", core, StringComparison.Ordinal);
            Assert.Contains("/ (CurrentDepth.x > 0.5 ? closestDepth : 1.0) + closestScreenMotion "
                + "* MovingSurfaceMotionFraction;", core, StringComparison.Ordinal);
            Assert.Contains("bool band = historyValid && !ownReprojected && nearerMoved", core,
                StringComparison.Ordinal);
            Assert.Contains("&& (movingEdge ? centreScreenMotion > closestScreenMotion", core,
                StringComparison.Ordinal);
            Assert.Contains(": travel > FollowedTravelRatio * closestScreenMotion);", core, StringComparison.Ordinal);
            Assert.Contains("band = band && !nearerNarrow;", core, StringComparison.Ordinal);
            Assert.Contains("centreScreenMotion = length(centreOwn * internalSize);", core, StringComparison.Ordinal);
            Assert.DoesNotContain("farthestDepth", core, StringComparison.Ordinal);
            Assert.Contains("|| (carriedBand && !nearerMoved && !(expectedDepth < footprint.nearest "
                + "* (1.0 - DisocclusionTolerance))\n                && followedElsewhere)", core,
                StringComparison.Ordinal);
            Assert.Contains("carriedBand = temporalStoredBand(carriedLeast);", core, StringComparison.Ordinal);
            Assert.Contains("bool followed = band && !movingEdge;", core, StringComparison.Ordinal);
            Assert.Contains("bool temporalStoredFollowed(float stored) { return stored < -4.5; }", core,
                StringComparison.Ordinal);
            Assert.Contains("carriedFollowed = temporalStoredFollowed(carriedLeast);", core, StringComparison.Ordinal);
            Assert.Contains("bool followedElsewhere = !carriedFollowed;", core, StringComparison.Ordinal);
            Assert.Contains("if (carriedFollowed && !nearerMoved && motionPixels > FollowedStillDisplayPixels) {", core,
                StringComparison.Ordinal);
            Assert.Contains("ivec2 historyTexel = ivec2(floor(previousUv * internalSize + jitter));", core,
                StringComparison.Ordinal);
            Assert.Contains("if (!(abs(shown.x) > MotionSentinel)) apart = max(apart, length(shown * internalSize "
                + "- ownMotion));", core, StringComparison.Ordinal);
            Assert.Contains("followedElsewhere = apart > FollowedHistoryMotionFraction * length(ownMotion);", core,
                StringComparison.Ordinal);
            int loopEnd = core.IndexOf("float reactive = clamp(toFloat(n.reactiveDifference)",
                StringComparison.Ordinal);
            int nine = core.IndexOf("if (carriedFollowed && !nearerMoved && motionPixels", StringComparison.Ordinal);
            int lastMotion = core.LastIndexOf("motionPixels = onScreen ?", StringComparison.Ordinal);
            int followedRead = core.IndexOf("carriedFollowed = temporalStoredFollowed(carriedLeast);",
                StringComparison.Ordinal);
            int footprintRead = core.IndexOf("DepthFootprint footprint = temporalDepthFootprint(",
                StringComparison.Ordinal);
            Assert.True(loopEnd >= 0 && followedRead > loopEnd && nine > followedRead && nine > lastMotion
                && footprintRead > nine,
                "the nine texels are read after the 3x3, the carried state and the pixel's own motion, "
                + "and before the depth test");
            int own = core.IndexOf("bool ownReprojected = ", StringComparison.Ordinal);
            int moved = core.IndexOf("bool nearerMoved = ", StringComparison.Ordinal);
            int band = core.IndexOf("bool band = historyValid", StringComparison.Ordinal);
            int reproject = core.IndexOf("if (ownReprojected) {", StringComparison.Ordinal);
            int carried = core.IndexOf("carriedBand = temporalStoredBand(carriedLeast);", StringComparison.Ordinal);
            int lockRead = core.IndexOf("s00.y = temporalStoredLock(s00.y);", StringComparison.Ordinal);
            int test = core.IndexOf("|| (carriedBand && !nearerMoved", StringComparison.Ordinal);
            Assert.True(own >= 0 && moved > own && band > moved && reproject > band && carried > reproject
                && lockRead > carried && test > lockRead, "the band reads the dilated travel before the own motion "
                + "replaces it, and its flag is read before the lock and the depth test");
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
            // own travel, which released a swaying blade and an avatar the camera follows. At a moving edge whose four
            // stored depths all lie farther than the moving surface, the lock read with that history is dropped before a
            // ridge can refresh it, so a nearer surface crossing a held line cannot carry it. The stored depths are read
            // there even where the depth test is skipped, and never drop a lock where nothing moves: an edge must pass an
            // absolute floor as well, above the float rounding of a sky texel's own motion under a still camera.
            string core = ShaderSources.TemporalResolveCoreGlsl;
            Assert.Contains("float lockValue = useHistory && !heldFromFarther "
                + "? max(historyState.y - LockDecay, 0.0) : 0.0;", core, StringComparison.Ordinal);
            Assert.Contains("bool movingEdge = edgeMotion > max(LockEdgeFloorInternalPixels,", core,
                StringComparison.Ordinal);
            Assert.Contains("length(closestMotion * internalSize) * LockEdgeMotionFraction);", core,
                StringComparison.Ordinal);
            Assert.Contains("if (historyValid && onScreen && (depthTested || movingEdge)) {", core, StringComparison.Ordinal);
            Assert.Contains("heldFromFarther = movingEdge && expectedDepth < footprint.nearest "
                + "* (1.0 - DisocclusionTolerance);", core, StringComparison.Ordinal);
            Assert.Contains("disoccluded = depthTested", core, StringComparison.Ordinal);
            Assert.Contains("disoccluded = depthTested && (!(expectedDepth > 1.0e-6)", core,
                StringComparison.Ordinal);
            int edge = core.IndexOf("bool movingEdge = ", StringComparison.Ordinal);
            int fetch = core.IndexOf("DepthFootprint footprint = temporalDepthFootprint(previousUv,",
                StringComparison.Ordinal);
            int refresh = core.IndexOf("if (ridge) lockValue = 1.0;", StringComparison.Ordinal);
            Assert.True(edge >= 0 && fetch > edge && refresh > fetch,
                "the edge signal must come before the stored depths it gates, and the drop before the ridge refresh");
            Assert.DoesNotMatch(@"\bParams\.x", core);
            Assert.Contains("vec4 carried = step(vec4(1.0e-3), bilinear);", core, StringComparison.Ordinal);
            Assert.Contains("max(max(carried.x * s00.y, carried.y * s10.y), max(carried.z * s01.y, carried.w * s11.y)));", core,
                StringComparison.Ordinal);
            Assert.DoesNotContain("textureLod(sampler2D(HistoryConfidence", core, StringComparison.Ordinal);
            Assert.Contains("edgeMotion = length((closestMotion - centreOwn) * internalSize);", core, StringComparison.Ordinal);
            Assert.Contains("* (1.0 - clamp((ownReprojected && useHistory ? 0.0 : edgeMotion) * LockEdgeRelease, 0.0, "
                + "1.0));", core, StringComparison.Ordinal);
            Assert.Contains("bool ownReprojected = !depthTested", core, StringComparison.Ordinal);
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
            // One rule for both, and every scene colour the 3x3 and the moving share weight goes through it.
            Assert.Contains("vec3 temporalWeighted(vec3 c) { return toWeighted(min(max(c, vec3(0.0)), "
                + "vec3(HalfMax))); }", core, StringComparison.Ordinal);
            Assert.Equal(2, core.Split("temporalWeighted(sceneColor.rgb)").Length - 1);
            Assert.Contains("temporalLuma(temporalWeighted(opaqueColor))", core, StringComparison.Ordinal);
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
            Assert.Contains("{ return kernels.x[x] * kernels.y[y]; }", core, StringComparison.Ordinal);
            Assert.Contains("float lanczosWeight = toFloat(temporalLanczosWeight(kernels, x, y));", core,
                StringComparison.Ordinal);
            Assert.Contains("clamp(kernels.displayX[x] * kernels.displayY[y], afloat(0.0), afloat(1.0))", core,
                StringComparison.Ordinal);
        }

        [Fact]
        public void Both_programs_read_their_inputs_without_gradients()
        {
            Assert.DoesNotContain("texture(sampler2D", ShaderSources.TemporalResolveCoreGlsl, StringComparison.Ordinal);
            Assert.DoesNotContain("texture(sampler2D", ShaderSources.TemporalDepthStoreFrag, StringComparison.Ordinal);
        }

        [Fact]
        public void The_core_exposes_the_pixel_function_the_debug_views_and_counts_re_evaluate()
        {
            string core = ShaderSources.TemporalResolveCoreGlsl;
            Assert.Contains("struct TemporalPixel {", core, StringComparison.Ordinal);
            Assert.Contains("vec3 color; float confidence; float stability; float moved; float disocclusion; "
                + "float reactive; float clip;", core, StringComparison.Ordinal);
            Assert.Contains("TemporalPixel temporalResolvePixel(ivec2 displayPixel) {", core, StringComparison.Ordinal);
            Assert.Contains("ivec2 temporalDisplaySize() {", core, StringComparison.Ordinal);
            Assert.Contains("uv = (vec2(displayPixel) + 0.5) / Sizes.zw;", core, StringComparison.Ordinal);
        }

        /// <summary>The clip flag the debug views and counts read marks a clip that moved the history, as blended, by
        /// more than the minimum in the weighted YCoCg space the clip runs in. The colour the resolve writes does not
        /// read the flag.</summary>
        [Fact]
        public void The_clip_flag_marks_only_a_clip_that_moved_the_history_by_more_than_the_minimum()
        {
            string core = ShaderSources.TemporalResolveCoreGlsl;
            Assert.Contains("result.clip = useHistory && clipScale > 1.0 && length(clipped - historyYcc) > "
                + "ClipFlagMinimumMove ? 1.0 : 0.0;", core, StringComparison.Ordinal);
            Assert.True(core.IndexOf("clipped.x = mix(clipped.x, historyYcc.x, hold);", StringComparison.Ordinal)
                < core.IndexOf("result.clip =", StringComparison.Ordinal), "the flag reads the history as blended");
        }

        [Fact]
        public void The_resolve_is_the_core_plus_a_main_that_writes_colour_and_state()
        {
            string frag = ShaderSources.TemporalResolveFrag;
            Assert.StartsWith("#version 450\n" + ShaderSources.TemporalFullPrecisionGlsl
                + ShaderSources.TemporalResolveCoreGlsl, frag, StringComparison.Ordinal);
            Assert.Contains("layout(location=0) out vec4 oColor;", frag, StringComparison.Ordinal);
            Assert.Contains("layout(location=1) out vec4 oState;", frag, StringComparison.Ordinal);
            Assert.Contains("oState = vec4(p.confidence, temporalStoreLock(p.stability, p.moved), 0.0, 1.0);", frag,
                StringComparison.Ordinal);
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
