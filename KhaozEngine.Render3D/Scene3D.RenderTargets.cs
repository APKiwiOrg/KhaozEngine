using System;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Sizing the model framebuffer and the pipelines drawn into it: the internal size, the MSAA sample count, the HDR
    /// colour format and, while temporal rendering is active, the motion attachment. Moved out of <c>Scene3D.cs</c>
    /// whole when the motion attachment joined the reasons a resize rebuilds the model pipelines, because that file is
    /// frozen by the size ratchet.
    /// </summary>
    public sealed partial class Scene3D
    {
        /// <summary>The anti-aliasing selection resolved against THIS device's capabilities (never throws): an MSAA
        /// request is clamped to a member of <see cref="GpuCapabilities.SupportedMsaaSampleCounts"/> or falls back
        /// to FXAA if the device cannot satisfy it. SSAA, FXAA and None pass through. Read fresh each frame (Post is
        /// mutable). Temporal rendering is single-sample, so while it is active an MSAA request falls back the same
        /// way.</summary>
        AntiAliasing ResolvedAa() => Post.EffectiveAaMode == AntiAliasingMode.None
            ? AntiAliasing.Off
            : Post.Quality.AntiAliasing.ResolveFor(_gd.Capabilities, TemporalActive);

        /// <summary>The MSAA sample count actually used this frame (1 = off), after device clamping.</summary>
        int ResolvedMsaaSamples()
        {
            AntiAliasing aa = ResolvedAa();
            return aa.Mode == AntiAliasingMode.Msaa ? aa.MsaaSamples : 1;
        }

        // Rebuild the pipelines of every renderer that draws into the model MRT or the colour-depth framebuffer, so
        // each pipeline matches its framebuffer's sample count, colour format and attachment count. Called only when
        // one of those changes (an MSAA, HDR or temporal toggle, all rare), never per frame. Material sets bind to each
        // renderer's layout (not the pipeline), so loaded meshes survive the rebuild.
        void RebuildMrtRenderers()
        {
            var modelOut = _res.ModelFB.Outputs;
            _model.SetOutputs(modelOut);
            _texBillboards.SetOutputs(modelOut);
            _beams.SetOutputs(modelOut);
            _trails.SetOutputs(modelOut);
            _overlayMeshes.SetOutputs(modelOut);
            _silhouettes.SetOutputs(modelOut);
            _decalRenderer.SetOutputs(_res.ColorDepthFB.Outputs);
            _particleRenderer.SetOutputs(_res.ColorDepthFB.Outputs);
            _sky.SetOutputs(_res.ColorDepthFB.Outputs);
            _starfield.SetOutputs(_res.ColorDepthFB.Outputs);
            _water.SetOutputs(_res.ColorDepthFB.Outputs);
            _depthLines.SetOutputs(_res.ColorDepthFB.Outputs);
        }

        /// <summary>
        /// The internal render-target size for a given post config + viewport. <see cref="RenderScale.FixedInternal"/>
        /// returns <see cref="PixelPostProcessSettings.RenderWidth"/>/<c>RenderHeight</c> unchanged (the historical
        /// path). <see cref="RenderScale.MatchViewport"/> tracks the viewport, clamped to
        /// <see cref="PixelPostProcessSettings.MaxRenderWidth"/>/<c>MaxRenderHeight</c> with aspect preserved, each
        /// dimension at least 1. Pure + headless-testable (no GPU). Stable once the viewport is at/over the cap for a
        /// fixed aspect, so <see cref="EnsureSize"/> doesn't thrash.
        /// </summary>
        internal static (int W, int H) ComputeTargetSize(PixelPostProcessSettings s, int viewportW, int viewportH)
        {
            // Read the AA-resolved sizing (AntiAliasing.Ssaa forces MatchViewport + its factor). AntiAliasing.Off
            // leaves these equal to the raw RenderScale/Supersample fields, so existing callers are unchanged.
            if (s.EffectiveRenderScale == RenderScale.FixedInternal)
                return (s.RenderWidth, s.RenderHeight);

            var (vw, vh, capScale) = ScaledViewport(s, viewportW, viewportH);
            if (capScale == 1f) return (vw, vh);
            int w = Math.Max(1, (int)MathF.Round(vw * capScale));
            int h = Math.Max(1, (int)MathF.Round(vh * capScale));
            return (w, h);
        }

        /// <summary>
        /// The viewport scaled by <see cref="PixelPostProcessSettings.EffectiveViewportScale"/> and rounded to whole
        /// pixels, with the <see cref="ViewportMath.Fit"/> scale that meets
        /// <see cref="PixelPostProcessSettings.MaxRenderWidth"/> by <see cref="PixelPostProcessSettings.MaxRenderHeight"/>,
        /// or a <c>CapScale</c> of 1 when the cap does not bite. <see cref="ComputeTargetSize"/> rounds the scaled size
        /// by it, and the temporal jitter cycle reads it unrounded (<see cref="TemporalDisplayOverInternal"/>).
        /// </summary>
        static (int W, int H, float CapScale) ScaledViewport(PixelPostProcessSettings s, int viewportW, int viewportH)
        {
            // MatchViewport renders at the framebuffer size x the supersample factor (SSAA), Temporal at the framebuffer
            // size x the upscale ratio, both capped (aspect-preserving downscale) so a huge window or big factor doesn't
            // allocate an unbounded target. Guard against a zero/negative viewport during startup/minimise.
            float ss = s.EffectiveViewportScale;
            int vw = Math.Max(1, (int)MathF.Round(Math.Max(1, viewportW) * ss));
            int vh = Math.Max(1, (int)MathF.Round(Math.Max(1, viewportH) * ss));
            int maxW = Math.Max(1, s.MaxRenderWidth);
            int maxH = Math.Max(1, s.MaxRenderHeight);
            return (vw, vh, vw <= maxW && vh <= maxH ? 1f : ViewportMath.Fit(vw, vh, maxW, maxH));
        }

        void EnsureSize(int viewportW, int viewportH)
        {
            LatchResolveForRender();   // whether this render resolves, before anything is sized (Scene3D.TemporalResolve.cs)
            var (tw, th) = ComputeTargetSize(Post, viewportW, viewportH);
            bool wantMips = WantsMipDownsample(Post, viewportW, viewportH);
            int samples = ResolvedMsaaSamples();
            // Under the resolve the post chain runs over the display targets, so the internal targets carry no bloom or ping
            // pair until a later render of a resolving frame presents through them. A resolving frame adds or drops either
            // pair in place, because recreating the targets would free ones an earlier render's commands still read.
            bool bloom = InternalBloomWanted, pings = InternalPingsWanted;
            bool sampleChanged = _res.SampleCount != samples;
            bool bloomChanged = _res.BloomAllocated != bloom && !_frameResolves;
            bool hdrChanged = _res.HdrColor != Post.Hdr.Enabled;
            // The motion attachment changes the model framebuffer's attachment COUNT, which every pipeline drawing into
            // it bakes, so gaining or losing it rebuilds them exactly like a sample-count or colour-format change.
            bool motion = TemporalActive;
            bool motionChanged = _res.MotionAllocated != motion;
            if (_res.Width != tw || _res.Height != th || _res.Mipped != wantMips || sampleChanged || bloomChanged || hdrChanged
                || motionChanged)
            {
                // A pipeline in flight may reference the old sample count / colour format / targets. A MSAA, HDR or
                // temporal toggle is rare, so idling before recreating the MRT + rebuilding pipelines is cheap insurance.
                bool rebuild = sampleChanged || hdrChanged || motionChanged;
                if (rebuild) _gd.WaitForIdle();
                _res.Resize(tw, th, wantMips, samples, bloom, Post.Hdr.Enabled, motion, pings);
                if (!_frameResolves) _post.BindTargets(_res);   // a resolving frame keeps _post on the display targets
                _transitions.BindTargets(_res);
                if (rebuild) RebuildMrtRenderers();   // match the renderers' pipelines to the new MRT
            }
            else
            {
                _res.EnsurePings(pings);
                _res.EnsureBloom(bloom);
            }
            // Aspect uses the true viewport (the post target is blit-stretched to fill it), not the clamped target.
            Camera.AspectRatio = viewportH > 0 ? (float)viewportW / viewportH : Camera.AspectRatio;
        }
    }
}
