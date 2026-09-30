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
    /// edge test unchanged, it runs only under the temporal anti-aliasing mode with the outline on, only then is its pass
    /// built and its internal targets held, and only the display chain after the resolve drops its own outline.
    /// </summary>
    public sealed class TemporalOutlineTests
    {
        [Fact]
        public void TheProgramIsTheEdgeOutlineAddressedByPixel()
        {
            string frag = ShaderSources.TemporalEdgeFrag;
            Assert.Contains("layout(location=0) in vec2 vUvIn;", frag, StringComparison.Ordinal);
            Assert.Contains("vec2 vUv = gl_FragCoord.xy * Texel.xy + vUvIn * 1.0e-30;", frag, StringComparison.Ordinal);
            Assert.Contains("vec2 nuv = vUv;", frag, StringComparison.Ordinal);
            Assert.DoesNotContain("1.0 - vUv.y", frag, StringComparison.Ordinal);
            int edgeTest = ShaderSources.EdgeFrag.IndexOf("    float edge = 0.0;", StringComparison.Ordinal);
            Assert.True(edgeTest > 0, "EdgeFrag no longer holds its edge test line");
            Assert.EndsWith(ShaderSources.EdgeFrag.Substring(edgeTest), frag, StringComparison.Ordinal);
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

        [Fact]
        public void TheSceneBuildsThePassOnlyWhenItRuns()
        {
            using var rig = new HeadlessSceneRig();
            rig.Scene.Post.Outline = true;
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Fxaa;
            rig.Frame(96, 64);
            rig.Frame(96, 64);
            Assert.False(rig.Scene.TemporalOutlineBuiltForTests);

            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Frame(96, 64);
            Assert.True(rig.Scene.TemporalOutlineBuiltForTests);
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
