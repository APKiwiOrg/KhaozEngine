using KhaozEngine.Gpu;

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

        void EnsureSize(int viewportW, int viewportH)
        {
            var (tw, th) = ComputeTargetSize(Post, viewportW, viewportH);
            bool wantMips = WantsMipDownsample(Post, viewportW, viewportH);
            int samples = ResolvedMsaaSamples();
            bool sampleChanged = _res.SampleCount != samples;
            bool bloomChanged = _res.BloomAllocated != Post.Bloom.Enabled;
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
                _res.Resize(tw, th, wantMips, samples, Post.Bloom.Enabled, Post.Hdr.Enabled, motion);
                _post.BindTargets(_res);
                _transitions.BindTargets(_res);
                if (rebuild) RebuildMrtRenderers();   // match the renderers' pipelines to the new MRT
            }
            // Aspect uses the true viewport (the post target is blit-stretched to fill it), not the clamped target.
            Camera.AspectRatio = viewportH > 0 ? (float)viewportW / viewportH : Camera.AspectRatio;
        }
    }
}
