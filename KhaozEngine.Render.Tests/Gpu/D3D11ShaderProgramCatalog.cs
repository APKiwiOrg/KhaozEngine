using System.Collections.Generic;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>One shipped graphics program: the vertex and fragment GLSL a renderer hands to
    /// <c>CreateShadersFromSpirv</c>.</summary>
    /// <param name="Name">A stable name for the program, used as the hash-table key.</param>
    /// <param name="VertexGlsl">The vertex source.</param>
    /// <param name="FragmentGlsl">The fragment source.</param>
    public readonly record struct ShippedGraphicsProgram(string Name, string VertexGlsl, string FragmentGlsl);

    /// <summary>One shipped compute kernel.</summary>
    /// <param name="Name">A stable name for the kernel, used as the hash-table key.</param>
    /// <param name="ComputeGlsl">The compute source.</param>
    public readonly record struct ShippedComputeKernel(string Name, string ComputeGlsl);

    /// <summary>
    /// EVERY SHADER PROGRAM THE ENGINE SHIPS, enumerated once so the shader-path tests assert against the real
    /// set rather than a sample of it. Derived from every non-test call site of
    /// <c>IGpuResourceFactory.CreateShadersFromSpirv</c> and <c>CreateComputeShaderFromSpirv</c>, which is
    /// exhaustive by construction: creating a shader is only possible through that factory, so no helper can hide
    /// a program from this list.
    ///
    /// <para>
    /// DEDUPLICATED BY SOURCE PAIR, not by call site. There are 36 call sites and 34 distinct pairs: the
    /// <c>Line</c> pair is created three times (<c>DepthLineRenderer</c>, and <c>LineRenderer</c> plus
    /// <c>FillRenderer</c> through <c>OverlayRenderer</c>), and identical sources cross-compile to identical
    /// HLSL, so three rows would be three copies of one fact. Where a pair has several call sites the name is its
    /// primary one and the others are noted below.
    /// </para>
    /// <para>
    /// WHEN A RENDERER GAINS OR LOSES A PROGRAM, ADD OR REMOVE A ROW HERE. Nothing detects the omission
    /// automatically, and that is a known limit rather than an oversight: a reflection-driven discovery would
    /// have to run the renderers, which needs a device, and the whole point of these tests is that they do not.
    /// The compensating check is that every name here also appears in the checked-in hash table, so a row added
    /// without a bake fails immediately and a row removed leaves an orphan the same test reports.
    /// </para>
    /// <para>
    /// The compute kernels are PARAMETERISED by cascade resolution. <c>WaterSeaState.CascadeResolution</c> is
    /// clamped to 32..256 and rounded down to a power of two, so the only values shipped code can reach are 32,
    /// 64, 128 and 256, and all four are listed. The default is 128.
    /// </para>
    /// </summary>
    public static class D3D11ShaderProgramCatalog
    {
        /// <summary>The cascade resolutions <c>OceanFftProducer</c> can compile a kernel for.</summary>
        public static readonly int[] OceanResolutions = { 32, 64, 128, 256 };

        /// <summary>Every distinct shipped vertex and fragment pair, 35 of them.</summary>
        public static IEnumerable<ShippedGraphicsProgram> GraphicsPrograms()
        {
            // Render2D.
            yield return new("Sprite2D", SpriteBatch.VertSrc, SpriteBatch.FragSrc);

            // Render3D model and terrain.
            yield return new("Model", ShaderSources.ModelVert, ShaderSources.ModelFrag);
            yield return new("ModelDissolve", ShaderSources.ModelVert, ShaderSources.ModelDissolveFrag);
            yield return new("SkinnedModel", ShaderSources.SkinnedModelVert, ShaderSources.SkinnedModelFrag);
            yield return new("SkinnedModelDissolve",
                ShaderSources.SkinnedModelVert, ShaderSources.SkinnedModelDissolveFrag);
            // The terrain pass. One half of decision S5's regression evidence: its interpolant ORDERING is what
            // keeps the fragment-used outputs a gap-free prefix.
            yield return new("Splat", ShaderSources.SplatVert, ShaderSources.SplatFrag);
            // The tile-world ground pass. Same lesson applied ahead of the incident: every interpolant it emits is
            // read by the fragment, so its pixel-input block is gap-free by construction.
            yield return new("TileGround", ShaderSources.TileGroundVert, ShaderSources.TileGroundFrag);

            // Render3D shadow atlas. The other half of the S5 evidence: every one of these vertex sources carries
            // the sink that stops SPIRV-Cross dropping a declared-but-unread input and holing the signature.
            yield return new("ShadowDepth", ShaderSources.ShadowDepthVert, ShaderSources.ShadowDepthFrag);
            yield return new("ShadowDepthDissolve",
                ShaderSources.ShadowDepthDissolveVert, ShaderSources.ShadowDepthDissolveFrag);
            yield return new("ShadowDepthDissolveInverted",
                ShaderSources.ShadowDepthDissolveVert, ShaderSources.ShadowDepthDissolveInvertedFrag);
            yield return new("SkinnedShadowDepth",
                ShaderSources.SkinnedShadowDepthVert, ShaderSources.ShadowDepthFrag);

            // Render3D effects and overlays.
            yield return new("Beam", ShaderSources.BeamVert, ShaderSources.BeamFrag);
            // Three call sites: DepthLineRenderer, and LineRenderer plus FillRenderer via OverlayRenderer.
            yield return new("Line", ShaderSources.LineVert, ShaderSources.LineFrag);
            yield return new("Billboard", ShaderSources.BillboardVert, ShaderSources.BillboardFrag);
            yield return new("TexturedBillboard",
                ShaderSources.BillboardVert, ShaderSources.TexturedBillboardFrag);
            yield return new("Particle", ShaderSources.ParticleVert, ShaderSources.ParticleFrag);
            yield return new("Trail", ShaderSources.TrailVert, ShaderSources.TrailFrag);
            yield return new("Distortion", ShaderSources.DistortionVert, ShaderSources.DistortionFrag);
            yield return new("GroundDecal", ShaderSources.DecalVert, ShaderSources.DecalFrag);
            yield return new("OverlayMesh", ShaderSources.OverlayUnlitVert, ShaderSources.OverlayUnlitFrag);

            // Render3D background and water.
            yield return new("Sky", ShaderSources.SkyVert, ShaderSources.SkyFrag);
            yield return new("Starfield", ShaderSources.StarfieldVert, ShaderSources.StarfieldFrag);
            yield return new("Water", ShaderSources.WaterVert, ShaderSources.WaterFrag);
            // Built lazily, only for a scene that asks for the clipmap grid, but it ships and it compiles.
            yield return new("WaterClipmap", ShaderSources.WaterClipmapVert, ShaderSources.WaterFrag);

            // Render3D fullscreen passes, all sharing FullscreenVert.
            yield return new("PostPalette", ShaderSources.FullscreenVert, ShaderSources.PaletteFrag);
            yield return new("PostEdge", ShaderSources.FullscreenVert, ShaderSources.EdgeFrag);
            yield return new("PostBlit", ShaderSources.FullscreenVert, ShaderSources.BlitFrag);
            yield return new("PostFxaa", ShaderSources.FullscreenVert, ShaderSources.FxaaFrag);
            yield return new("PostTonemap", ShaderSources.FullscreenVert, ShaderSources.TonemapFrag);
            yield return new("PostDistortionApply",
                ShaderSources.FullscreenVert, ShaderSources.DistortionApplyFrag);
            yield return new("PostBloomBright", ShaderSources.FullscreenVert, ShaderSources.BloomBrightFrag);
            yield return new("PostBloomBlur", ShaderSources.FullscreenVert, ShaderSources.BloomBlurFrag);
            yield return new("PostBloomComposite",
                ShaderSources.FullscreenVert, ShaderSources.BloomCompositeFrag);
            yield return new("TransitionSolid", ShaderSources.FullscreenVert, ShaderSources.TransitionSolidFrag);
            yield return new("TransitionCrossfade",
                ShaderSources.FullscreenVert, ShaderSources.TransitionCrossfadeFrag);
        }

        /// <summary>Every shipped compute kernel, across the four reachable cascade resolutions.</summary>
        public static IEnumerable<ShippedComputeKernel> ComputeKernels()
        {
            foreach (int n in OceanResolutions)
            {
                yield return new($"OceanFftRowPass{n}", OceanComputeShaders.RowPass(n));
                yield return new($"OceanFftColumnPass{n}", OceanComputeShaders.ColumnPass(n));
            }
        }
    }
}
