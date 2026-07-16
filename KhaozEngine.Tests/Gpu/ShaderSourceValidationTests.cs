using KhaozEngine.Gpu;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Device-free validation of every embedded production shader pair via <see cref="ShaderValidation"/>. These are
    /// plain [Fact]s (NOT [GpuFact]), so they run in the fast GPU-free ci.yml loop on every push: a GLSL syntax error
    /// or a backend cross-compile miscompile now fails the build instead of only surfacing at first run on a real
    /// device of that backend. The pairs mirror how the renderers actually create pipelines from ShaderSources:
    /// <list type="bullet">
    /// <item>ModelVert+ModelFrag, SplatVert+SplatFrag (ModelRenderer)</item>
    /// <item>ShadowDepthVert+ShadowDepthFrag (ShadowMapRenderer depth-only pass)</item>
    /// <item>LineVert+LineFrag (LineRenderer via OverlayRenderer)</item>
    /// <item>BillboardVert+BillboardFrag (BillboardRenderer via OverlayRenderer)</item>
    /// <item>BillboardVert+TexturedBillboardFrag (TexturedBillboardRenderer - reuses BillboardVert)</item>
    /// <item>ParticleVert+ParticleFrag (ParticleRenderer modern particle sprites)</item>
    /// <item>BeamVert+BeamFrag (BeamRenderer)</item>
    /// <item>OverlayUnlitVert+OverlayUnlitFrag (OverlayMeshRenderer)</item>
    /// <item>DecalVert+DecalFrag (GroundDecalRenderer)</item>
    /// <item>SkyVert+SkyFrag (SkyRenderer background pass)</item>
    /// <item>WaterVert+WaterFrag (WaterRenderer animated water surface)</item>
    /// <item>FullscreenVert paired with each post fragment PaletteFrag/EdgeFrag/BlitFrag/FxaaFrag/BloomBrightFrag/
    /// BloomBlurFrag/BloomCompositeFrag (PixelPostProcess)</item>
    /// <item>FullscreenVert+TransitionSolidFrag/TransitionCrossfadeFrag (TransitionRenderer), ModelVert+ModelDissolveFrag (CharDissolve)</item>
    /// <item>SpriteBatch VertSrc+FragSrc (Render2D)</item>
    /// </list>
    /// </summary>
    public sealed class ShaderSourceValidationTests
    {
        [Fact]
        public void Model()
            => ShaderValidation.ValidatePair(ShaderSources.ModelVert, ShaderSources.ModelFrag, "Model");

        [Fact]
        public void Splat()
            => ShaderValidation.ValidatePair(ShaderSources.SplatVert, ShaderSources.SplatFrag, "Splat");

        [Fact]
        public void ShadowDepth()
            => ShaderValidation.ValidatePair(ShaderSources.ShadowDepthVert, ShaderSources.ShadowDepthFrag, "ShadowDepth");

        [Fact]
        public void Line()
            => ShaderValidation.ValidatePair(ShaderSources.LineVert, ShaderSources.LineFrag, "Line");

        [Fact]
        public void Billboard()
            => ShaderValidation.ValidatePair(ShaderSources.BillboardVert, ShaderSources.BillboardFrag, "Billboard");

        [Fact]
        public void TexturedBillboard()
            => ShaderValidation.ValidatePair(ShaderSources.BillboardVert, ShaderSources.TexturedBillboardFrag, "TexturedBillboard");

        [Fact]
        public void Particle()
            => ShaderValidation.ValidatePair(ShaderSources.ParticleVert, ShaderSources.ParticleFrag, "Particle");

        [Fact]
        public void Distortion()
            => ShaderValidation.ValidatePair(ShaderSources.DistortionVert, ShaderSources.DistortionFrag, "Distortion");

        [Fact]
        public void Beam()
            => ShaderValidation.ValidatePair(ShaderSources.BeamVert, ShaderSources.BeamFrag, "Beam");

        [Fact]
        public void OverlayUnlit()
            => ShaderValidation.ValidatePair(ShaderSources.OverlayUnlitVert, ShaderSources.OverlayUnlitFrag, "OverlayUnlit");

        [Fact]
        public void Decal()
            => ShaderValidation.ValidatePair(ShaderSources.DecalVert, ShaderSources.DecalFrag, "Decal");

        [Fact]
        public void Sky()
            => ShaderValidation.ValidatePair(ShaderSources.SkyVert, ShaderSources.SkyFrag, "Sky");

        [Fact]
        public void Water()
            => ShaderValidation.ValidatePair(ShaderSources.WaterVert, ShaderSources.WaterFrag, "Water");

        [Fact]
        public void PostPalette()
            => ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, ShaderSources.PaletteFrag, "PostPalette");

        [Fact]
        public void PostEdge()
            => ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, ShaderSources.EdgeFrag, "PostEdge");

        [Fact]
        public void PostBlit()
            => ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, ShaderSources.BlitFrag, "PostBlit");

        [Fact]
        public void PostFxaa()
            => ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, ShaderSources.FxaaFrag, "PostFxaa");

        [Fact]
        public void Tonemap()
            => ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, ShaderSources.TonemapFrag, "Tonemap");

        [Fact]
        public void PostBloomBright()
            => ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, ShaderSources.BloomBrightFrag, "PostBloomBright");

        [Fact]
        public void PostBloomBlur()
            => ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, ShaderSources.BloomBlurFrag, "PostBloomBlur");

        [Fact]
        public void PostBloomComposite()
            => ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, ShaderSources.BloomCompositeFrag, "PostBloomComposite");

        [Fact]
        public void TransitionSolid()
            => ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, ShaderSources.TransitionSolidFrag, "TransitionSolid");

        [Fact]
        public void TransitionCrossfade()
            => ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, ShaderSources.TransitionCrossfadeFrag, "TransitionCrossfade");

        [Fact]
        public void ModelDissolve()
            => ShaderValidation.ValidatePair(ShaderSources.ModelVert, ShaderSources.ModelDissolveFrag, "ModelDissolve");

        [Fact]
        public void SkinnedModel()
            => ShaderValidation.ValidatePair(ShaderSources.SkinnedModelVert, ShaderSources.SkinnedModelFrag, "SkinnedModel");

        [Fact]
        public void SkinnedModelDissolve()
            => ShaderValidation.ValidatePair(ShaderSources.SkinnedModelVert, ShaderSources.SkinnedModelDissolveFrag, "SkinnedModelDissolve");

        [Fact]
        public void SkinnedShadowDepth()
            => ShaderValidation.ValidatePair(ShaderSources.SkinnedShadowDepthVert, ShaderSources.ShadowDepthFrag, "SkinnedShadowDepth");

        [Fact]
        public void Sprite2D()
            => ShaderValidation.ValidatePair(SpriteBatch.VertSrc, SpriteBatch.FragSrc, "Sprite2D");

        [Fact]
        public void BrokenSourceThrows()
        {
            // A deliberate GLSL syntax error (undeclared identifier, missing type) must fail validation, proving the
            // validator actually compiles the sources rather than waving them through.
            const string brokenVert = @"#version 450
void main() { gl_Position = notAThing * 2.0; }";
            const string brokenFrag = @"#version 450
layout(location=0) out vec4 oColor;
void main() { oColor = vec4(1.0); }";

            Assert.Throws<ShaderValidationException>(
                () => ShaderValidation.ValidatePair(brokenVert, brokenFrag, "Broken"));
        }
    }
}
