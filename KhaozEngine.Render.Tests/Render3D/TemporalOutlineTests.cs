using System;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The edge outline ahead of the temporal resolve, device free: the program is EdgeFrag addressed by pixel with the
    /// edge test unchanged, run once for both images, it runs only under the temporal anti-aliasing mode with the
    /// outline on, only then is its pass built and its internal targets held, it is released when it stops running, and
    /// only the display chain after the resolve drops its own outline.
    /// </summary>
    public sealed class TemporalOutlineTests
    {
        [Fact]
        public void TheProgramIsTheEdgeOutlineAddressedByPixelWritingBothImages()
        {
            string edge = ShaderSources.EdgeFrag, frag = ShaderSources.TemporalEdgeFrag;
            // EdgeFrag's declarations up to its header comment, and everything from the lit colour's first use to
            // the line before its output (the normal and depth taps and the edge test), are spliced in verbatim.
            Assert.StartsWith(edge.Substring(0, edge.IndexOf("// Texel.xy=1/size", StringComparison.Ordinal)), frag,
                StringComparison.Ordinal);
            int body = edge.IndexOf("    vec3 base = baseSrc.rgb;", StringComparison.Ordinal);
            int output = edge.IndexOf("    oColor = ", StringComparison.Ordinal);
            Assert.True(body > 0 && output > body, "EdgeFrag no longer holds its body between these lines");
            Assert.Contains(edge.Substring(body, output - body), frag, StringComparison.Ordinal);

            Assert.Contains("layout(set=0, binding=5) uniform texture2D OpaqueTex;", frag, StringComparison.Ordinal);
            Assert.Contains("layout(location=0) in vec2 vUvIn;", frag, StringComparison.Ordinal);
            Assert.Contains("layout(location=0) out vec4 oScene;", frag, StringComparison.Ordinal);
            Assert.Contains("layout(location=1) out vec4 oOpaque;", frag, StringComparison.Ordinal);
            Assert.Contains("vec2 vUv = gl_FragCoord.xy * Texel.xy + vUvIn * 1.0e-30;", frag, StringComparison.Ordinal);
            Assert.Contains("vec2 nuv = vUv;", frag, StringComparison.Ordinal);
            Assert.Contains("oOpaque = vec4(mix(opaqueSrc.rgb, OutlineColor.rgb, edge), opaqueSrc.a);", frag,
                StringComparison.Ordinal);
            // No parity flip, and no word of it: the header comment that described it is spliced out too.
            Assert.DoesNotContain("Fade.z", frag, StringComparison.Ordinal);
            Assert.DoesNotContain("oColor", frag, StringComparison.Ordinal);
            // One edge test for both images.
            Assert.Equal(1, Count(frag, "    float edge = 0.0;"));
        }

        static int Count(string text, string part)
        {
            int n = 0;
            for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
                n++;
            return n;
        }

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        public void TheOutlineRunsAheadOfTheResolveOnlyUnderTemporalAntiAliasing(bool outline, bool temporal, bool ahead)
        {
            var s = new PixelPostProcessSettings { Outline = outline };
            s.Quality.AntiAliasing = temporal ? AntiAliasing.Temporal : AntiAliasing.Fxaa;
            Assert.Equal(ahead, PixelPostProcess.TemporalOutlineRuns(s));
            s.Pixelated = true;   // Pixelated refuses temporal anti-aliasing, so the chain keeps the outline
            Assert.False(PixelPostProcess.TemporalOutlineRuns(s));
        }

        [Fact]
        public void OnlyTheDisplayChainAfterTheResolveDropsItsOwnOutline()
        {
            using var device = new FakeGpuDevice();
            using var internalTargets = new RenderResources(device, 96, 64, hdrColor: true);
            using var displayTargets = new TemporalPostTargets(device);
            var s = new PixelPostProcessSettings { Outline = true };
            s.Quality.AntiAliasing = AntiAliasing.Temporal;
            Assert.False(PixelPostProcess.OutlineRunsInChain(s, displayTargets));
            // A later render inside a resolving frame runs the internal chain without a resolve, so it keeps it.
            Assert.True(PixelPostProcess.OutlineRunsInChain(s, internalTargets));

            s.Quality.AntiAliasing = AntiAliasing.Fxaa;
            Assert.True(PixelPostProcess.OutlineRunsInChain(s, internalTargets));
            s.Outline = false;
            Assert.False(PixelPostProcess.OutlineRunsInChain(s, internalTargets));
        }

        /// <summary>The pass is built on the first resolving frame with the outline on, as one draw into both internal
        /// pings through one pipeline with two colour outputs, and released again on the first resolving frame without
        /// the outline and on the first frame without the resolve.</summary>
        [Fact]
        public void TheSceneBuildsThePassOnlyWhileItRunsAndReleasesItAfter()
        {
            using var rig = new HeadlessSceneRig();
            Scene3D scene = rig.Scene;
            scene.Post.Outline = true;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Fxaa;
            rig.Frame(96, 64);
            rig.Frame(96, 64);
            Assert.False(scene.TemporalOutlineBuiltForTests);

            scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Frame(96, 64);
            Assert.True(scene.TemporalOutlineBuiltForTests);
            int withOutline = scene.LastFrameStats.DrawCalls;
            FakeGraphicsPipelineRequest pipeline = Assert.Single(rig.Factory.GraphicsPipelines,
                p => p.FragmentGlsl == ShaderSources.TemporalEdgeFrag);
            Assert.Equal(2, pipeline.Description.Outputs.Colour.Length);

            scene.Post.Outline = false;   // a resolving frame without the outline lets the pass go
            rig.Frame(96, 64);
            Assert.True(scene.ResolvedLastRenderForTests);
            Assert.False(scene.TemporalOutlineBuiltForTests);
            Assert.Equal(1, PixelPostProcess.TemporalOutlineDrawCalls);
            Assert.Equal(PixelPostProcess.TemporalOutlineDrawCalls, withOutline - scene.LastFrameStats.DrawCalls);

            scene.Post.Outline = true;
            rig.Frame(96, 64);
            Assert.True(scene.TemporalOutlineBuiltForTests);
            scene.Post.Quality.AntiAliasing = AntiAliasing.Fxaa;   // so does a frame without the resolve
            rig.Frame(96, 64);
            Assert.False(scene.ResolvedLastRenderForTests);
            Assert.False(scene.TemporalOutlineBuiltForTests);
        }

        /// <summary>Under the resolve the internal ping pair is free of the post chain, which runs on the display
        /// targets, so the outline ahead of the resolve writes it. The pair is held only while the outline runs there,
        /// and turning the outline off lets it go again.</summary>
        [Fact]
        public void TheResolveHoldsTheInternalPingsOnlyWhileTheOutlineRunsAheadOfIt()
        {
            using var rig = new HeadlessSceneRig();
            Scene3D scene = rig.Scene;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Frame(96, 64);
            rig.Frame(96, 64);
            Assert.True(scene.ResolvedLastRenderForTests);
            Assert.False(scene.InternalPingsAllocatedForTests);

            scene.Post.Outline = true;
            rig.Frame(96, 64);
            Assert.True(scene.ResolvedLastRenderForTests);
            Assert.True(scene.InternalPingsAllocatedForTests, "the outline ahead of the resolve has no target to write");

            scene.Post.Outline = false;
            rig.Frame(96, 64);
            Assert.False(scene.InternalPingsAllocatedForTests);
        }
    }
}
