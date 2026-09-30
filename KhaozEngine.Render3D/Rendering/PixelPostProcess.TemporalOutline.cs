using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// THE EDGE OUTLINE AHEAD OF THE TEMPORAL RESOLVE. Under <see cref="AntiAliasing.Temporal"/> the post chain runs at
    /// display resolution after the resolve, where the edge pass would read the jittered internal normal and depth and
    /// its one-pixel lines would move with the jitter. So under temporal anti-aliasing the outline runs here instead,
    /// on the internal lit colour and on the opaque-only copy, before the resolve reads them, and the display chain
    /// skips it. <see cref="ShaderSources.TemporalEdgeFrag"/> addresses pixels by <c>gl_FragCoord</c>, so the outlined
    /// images keep the orientation the resolve reads and no chain parity counts the pass. A later render inside a
    /// resolving frame, which runs the internal chain without a resolve, and every other mode run the outline in the
    /// chain exactly as before, and nothing here is built.
    /// </summary>
    internal sealed partial class PixelPostProcess
    {
        /// <summary>The draws <see cref="RunTemporalOutline"/> records: the lit colour and the opaque-only copy.</summary>
        internal const int TemporalOutlineDrawCalls = 2;

        IGpuPipeline? _temporalEdgePipe;
        GpuOutputDescription _temporalEdgeOutput;
        IGpuResourceSet? _temporalEdgeFromColor, _temporalEdgeFromOpaque;
        IGpuTexture? _temporalEdgeColor, _temporalEdgeOpaque, _temporalEdgeNormal, _temporalEdgeDepth;

        /// <summary>Whether the outline runs ahead of the resolve this frame: it is on and the effective anti-aliasing
        /// mode is <see cref="AntiAliasingMode.Temporal"/>, which <see cref="PixelPostProcessSettings.Pixelated"/>
        /// refuses.</summary>
        internal static bool TemporalOutlineRuns(PixelPostProcessSettings s) =>
            s.Outline && s.EffectiveAaMode == AntiAliasingMode.Temporal;

        /// <summary>Whether a chain over <paramref name="res"/> runs its own outline pass: the outline is on, unless the
        /// targets are the display targets after the resolve (<see cref="TemporalPostTargets"/>) and the outline ran
        /// ahead of it. The scene hands those targets only to the render that resolves, so a later render's internal
        /// chain keeps its outline.</summary>
        internal static bool OutlineRunsInChain(PixelPostProcessSettings s, IPostChainTargets res) =>
            s.Outline && !(res is TemporalPostTargets && TemporalOutlineRuns(s));

        /// <summary>Whether the pass ahead of the resolve has been built. Internal, for the tests.</summary>
        internal bool TemporalOutlineBuilt => _temporalEdgePipe != null;

        /// <summary>
        /// Outline the internal lit colour into <c>res.PingA</c> and the opaque-only copy into <c>res.PingB</c>, the two
        /// images the resolve reads as its scene and opaque colour this frame. Reads the edge block
        /// <see cref="PrepareUniforms"/> uploaded this frame, whose texel size is the internal normal target's. Outlining
        /// both keeps the reactive estimate from reading the lines as transparent content, because the edge mask depends
        /// only on the normal and depth both images share. Call after every internal-resolution colour writer and
        /// before the resolve. Each draw binds its own framebuffer.
        /// </summary>
        internal void RunTemporalOutline(IGpuCommandList cl, RenderResources res, IGpuTexture opaque)
        {
            if (!res.PingsAllocated)
                throw new InvalidOperationException("The outline ahead of the temporal resolve writes the internal "
                    + "ping pair, which is not allocated. The scene holds it while this pass runs.");
            IGpuResourceFactory f = _gd.Factory;
            GpuOutputDescription output = res.PingAFB.Outputs;
            if (_temporalEdgePipe is null || !SamePingFormat(output, _temporalEdgeOutput))
            {
                _temporalEdgePipe?.Dispose();
                _temporalEdgePipe = FullscreenPipeline(f, Pair(f, ShaderSources.TemporalEdgeFrag), _edgeLayout, output);
                _temporalEdgeOutput = output;
            }
            // Keyed on the textures themselves, as the resolve's own sets are, so a change elsewhere in the internal
            // targets, such as the bloom pair coming or going, rebuilds nothing.
            if (!ReferenceEquals(res.ColorTex, _temporalEdgeColor) || !ReferenceEquals(opaque, _temporalEdgeOpaque)
                || !ReferenceEquals(res.NormalTex, _temporalEdgeNormal)
                || !ReferenceEquals(res.DepthColorTex, _temporalEdgeDepth))
            {
                DisposeTemporalOutlineSets();
                _temporalEdgeFromColor = f.CreateResourceSet(new GpuResourceSetDescription(_edgeLayout,
                    res.ColorTex, res.NormalTex, res.DepthColorTex, _gd.PointSampler, _edgeBuf));
                _temporalEdgeFromOpaque = f.CreateResourceSet(new GpuResourceSetDescription(_edgeLayout,
                    opaque, res.NormalTex, res.DepthColorTex, _gd.PointSampler, _edgeBuf));
                _temporalEdgeColor = res.ColorTex;
                _temporalEdgeOpaque = opaque;
                _temporalEdgeNormal = res.NormalTex;
                _temporalEdgeDepth = res.DepthColorTex;
            }

            cl.SetFramebuffer(res.PingAFB);
            cl.SetPipeline(_temporalEdgePipe);
            cl.SetGraphicsResourceSet(0, _temporalEdgeFromColor!);
            cl.Draw(3);

            cl.SetFramebuffer(res.PingBFB);
            cl.SetPipeline(_temporalEdgePipe);
            cl.SetGraphicsResourceSet(0, _temporalEdgeFromOpaque!);
            cl.Draw(3);
        }

        // From Dispose. The shader set belongs to _shaderCache, which Dispose already empties.
        void DisposeTemporalOutline()
        {
            DisposeTemporalOutlineSets();
            _temporalEdgePipe?.Dispose();
            _temporalEdgePipe = null;
        }

        void DisposeTemporalOutlineSets()
        {
            _temporalEdgeFromColor?.Dispose();
            _temporalEdgeFromOpaque?.Dispose();
            _temporalEdgeFromColor = _temporalEdgeFromOpaque = null;
            _temporalEdgeColor = _temporalEdgeOpaque = _temporalEdgeNormal = _temporalEdgeDepth = null;
        }
    }
}
