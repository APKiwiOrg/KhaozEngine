using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The temporal resolve's frame wiring (docs/design/TEMPORAL-RESOLVE-UPSCALING-DESIGN-2026-09-24.md, sections 1 and
    /// 2). <see cref="AntiAliasing.Temporal"/> turns temporal rendering on, sizes the internal target as a fraction of the
    /// display, and sets the jitter cycle from the display over internal scale. It also owns the resolve's renderer and
    /// display targets, runs the resolve, and reorders the background ahead of the transparent model-pass writers while
    /// it runs. A partial of its own because <c>Scene3D.cs</c> is frozen by the file-size ratchet.
    /// <para>
    /// A frame resolves on its first render only, which fixes the decision for the frame as it fixes the temporal state
    /// (<see cref="TemporalActive"/>). A later render inside the frame, such as an offscreen capture, may use another
    /// camera or size, while its motion pairs its own view with the previous view the frame's first render latched, so
    /// reading history through that motion would smear. It never resolves. It presents its internal frame unresolved
    /// through the internal post chain, without bloom, and leaves the history targets, their pair and their contents
    /// alone (docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md, section 2: a second render does not advance
    /// history).
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        // The display size the frame's first render latched, for the jitter cycle and the history key.
        int _latchedDisplayWidth, _latchedDisplayHeight;

        /// <summary>Whether temporal anti-aliasing is in effect: the effective anti-aliasing mode is
        /// <see cref="AntiAliasingMode.Temporal"/>, which <see cref="PixelPostProcessSettings.Pixelated"/> refuses. It
        /// requests temporal rendering and sizes the internal target by
        /// <see cref="PixelPostProcessSettings.EffectiveUpscaleRatio"/>. A live read of the settings. The frame's
        /// resolve decision is the value this had at the frame's first render.</summary>
        internal bool TemporalResolveActive => Post.EffectiveAaMode == AntiAliasingMode.Temporal;

        /// <summary>The display over internal scale per axis for the latched display size, which sets the jitter
        /// sequence length. <see cref="LatchFrameView"/> reads it on the frame's first render and fixes the sequence
        /// length it gives for the frame, which <see cref="LastTemporalDiagnostics"/> reports. 1 unless temporal
        /// anti-aliasing is in effect.</summary>
        float DisplayOverInternalRatio => TemporalDisplayOverInternal(_latchedDisplayWidth, _latchedDisplayHeight);

        /// <summary>The display over internal scale per axis for a display of <paramref name="displayWidth"/> by
        /// <paramref name="displayHeight"/> while temporal anti-aliasing is in effect, else exactly 1. The scale the
        /// jitter cycle needs, unrounded with the render cap included, as <see cref="TemporalJitter.PhaseCount"/>
        /// defines it.</summary>
        internal float TemporalDisplayOverInternal(int displayWidth, int displayHeight)
        {
            if (!TemporalResolveActive || displayWidth <= 0 || displayHeight <= 0) return 1f;
            return 1f / (Post.EffectiveUpscaleRatio * ScaledViewport(Post, displayWidth, displayHeight).CapScale);
        }

        // The resolve's renderer and display targets, created on the first frame the resolve runs. The first frame
        // without it releases the display targets and the renderer's sets and keeps both objects.
        TemporalResolveRenderer? _temporalResolve;
        TemporalPostTargets? _temporalPost;

        /// <summary>The resolve's renderer, null until the first frame that resolves. Internal, for the tests.</summary>
        internal TemporalResolveRenderer? TemporalResolveRendererForTests => _temporalResolve;

        /// <summary>The display post targets, null until the first frame that resolves and released, but kept, by a frame
        /// that does not. Internal, for the tests.</summary>
        internal TemporalPostTargets? TemporalPostTargetsForTests => _temporalPost;

        // Whether this frame resolves, fixed by its first render.
        bool _frameResolves;

        // Whether the render in progress runs the resolve: the first render of a resolving frame.
        bool _resolveThisRender;

        // Whether a later render inside a resolving frame has presented through the internal chain. It keeps the internal
        // ping pair until the resolve stops, so a host that captures every frame does not reallocate it every frame.
        bool _internalChainKept;

        /// <summary>Whether the last render ran the resolve. Internal, for the tests.</summary>
        internal bool ResolvedLastRenderForTests => _resolveThisRender;

        /// <summary>Whether the internal targets carry the post chain's ping pair. Internal, for the tests.</summary>
        internal bool InternalPingsAllocatedForTests => _res.PingsAllocated;

        // The internal targets' bloom pair, which only the internal chain reads, so never while the frame resolves.
        bool InternalBloomWanted => Post.Bloom.Enabled && !_frameResolves;

        // The internal targets' ping pair, which a resolving frame needs only for a later render's internal chain.
        bool InternalPingsWanted => !_frameResolves || _internalChainKept;

        /// <summary>From <c>EnsureSize</c> at the start of every render, before anything is sized or latched: fix the
        /// frame's resolve decision on its first render, and whether this render runs it. The first render reads the
        /// live selection, as <see cref="LatchFrameView"/> reads the live temporal requesters right after, so the two
        /// agree. A later render reads the fixed value.</summary>
        void LatchResolveForRender()
        {
            bool first = !_frameViewLatchedThisFrame;
            if (first) _frameResolves = TemporalResolveActive;
            _resolveThisRender = first && _frameResolves;
            if (!_frameResolves) _internalChainKept = false;
            else if (!first) _internalChainKept = true;
        }

        /// <summary>
        /// Before any framebuffer is bound this render: size the history and the display targets, choose the history
        /// pair, bind the resolve's inputs and upload its uniforms, and hand back the targets the post chain runs over.
        /// Every target the post chain reads is final when this returns, so the chain can bind them next and run them
        /// unchanged. On a frame without the resolve it lets go of whatever a previous resolving frame left and hands back
        /// <c>_res</c>, the internal chain. A later render inside a resolving frame also hands back <c>_res</c> and leaves
        /// the history alone.
        /// </summary>
        IPostChainTargets PrepareTemporalResolve(IGpuCommandList cl, int displayWidth, int displayHeight)
        {
            if (!_frameResolves)
            {
                ReleaseTemporalResolve();
                return _res;
            }
            if (!_resolveThisRender) return _res;

            FrameView current = _currentFrameView;
            _temporalResolve ??= new TemporalResolveRenderer(_gd);
            _temporalPost ??= new TemporalPostTargets(_gd);
            // New targets hold nothing. A new display or internal size already reset the history through the frame key,
            // so this catches a replacement the key cannot see, and the resolve never reads new targets as history.
            if (TemporalHistory.EnsureTargets(_gd, displayWidth, displayHeight, _res.Width, _res.Height, _retired)
                && TemporalHistory.IsValid)
            {
                TemporalHistory.Invalidate(TemporalResetReason.Resize);
                LastTemporalDiagnostics = LastTemporalDiagnostics with
                {
                    HistoryValid = false,
                    LastReset = TemporalHistory.LastReset,
                };
            }
            _temporalPost.Ensure(_res, TemporalHistory, displayWidth, displayHeight, Post.Bloom.Enabled);

            TemporalHistory.BeginResolve(current.FrameIndex);
            IGpuTexture motion = _res.MotionTex ?? throw new InvalidOperationException(
                "The temporal resolve runs only while temporal rendering is active, which allocates the motion target.");
            _temporalResolve.BindInputs(new TemporalResolveInputs(_res.ColorTex, _temporalPost.OpaqueColor,
                _res.DepthColorTex, motion), TemporalHistory);

            // The previous view is the one the motion target reprojects with (Scene3D.MotionTarget.cs): the last frame's
            // first render rebased to this frame's origin, or null without history.
            FrameView? previous = PreviousFrameView;
            TemporalResolveUniforms uniforms = TemporalResolveMath.BuildUniforms(ViewInput(current),
                previous is { } last ? ViewInput(last) : null, current.JitterPixels, current.Width, current.Height,
                displayWidth, displayHeight, TemporalHistory.IsValid);
            _temporalResolve.PrepareUniforms(cl, uniforms, TemporalResolveMath.BuildDepthStore(current.Projection));
            return _temporalPost;
        }

        /// <summary>
        /// When this render resolves, after the opaque passes and before the transparent model-pass writers: draw the
        /// background, copy the lit colour for the reactive estimate, and rebind the model target for the writers that
        /// follow. The background moves up because the copy must hold the sky, and the sky's Equal depth test and
        /// override blend would otherwise paint over any transparent drawn before it
        /// (docs/design/TEMPORAL-RESOLVE-UPSCALING-DESIGN-2026-09-24.md, plan amendment 5).
        /// </summary>
        void CaptureOpaqueForTemporal(IGpuCommandList cl)
        {
            if (!_resolveThisRender) return;
            DrawBackground(cl);
            // A framebuffer change ends the model pass, so a clear with no draw after it (a Solid background with
            // nothing opaque) lands before the copy. A blit alone leaves a clear-only pass's clear owed.
            cl.SetFramebuffer(_res.ColorDepthFB);
            _temporalPost!.CopyOpaque(cl);
            cl.SetFramebuffer(_res.ModelFB);
        }

        /// <summary>
        /// Background pass: whichever mode is selected paints the no-geometry pixels and marks them alpha 1. Mutually
        /// exclusive by construction (Post.Background derives the sky-over-starfield precedence), so at most one of these
        /// runs, and Solid runs neither. The far-plane sky triangle passes the Equal read-only depth test ONLY where the
        /// stored depth still EQUALS the cleared far plane (background where no mesh drew), so it fills the gradient + sun
        /// there and geometry pixels (depth &lt; 1) reject it. Both passes write only the colour attachment (never the MRT
        /// normal/linear-depth the outline pass reads) with alpha 1, marking those pixels as opaque painted background.
        /// Fully skipped when Solid, so a Solid frame renders byte-identical to before this pass existed. Moved here from
        /// <c>RenderInternal</c> unchanged.
        /// </summary>
        void DrawBackground(IGpuCommandList cl)
        {
            switch (Post.Background)
            {
                case BackgroundMode.Sky:
                    _sky.Draw(cl, _res, _currentFrameView.View, _currentFrameView.JitteredProjection, Post.LightDirection, Post.Sky);
                    _frameStats.DrawCalls++;
                    break;
                case BackgroundMode.Starfield:
                    _starfield.Draw(cl, _res, Post.BackgroundColor);
                    _frameStats.DrawCalls++;
                    break;
            }
        }

        /// <summary>When this render resolves, after every internal-resolution colour writer and the distortion field and
        /// before the post chain: resolve into the history write pair, then store this frame's depth.</summary>
        void RunTemporalResolve(IGpuCommandList cl)
        {
            if (!_resolveThisRender) return;
            _temporalResolve!.Run(cl, TemporalHistory);
            _frameStats.DrawCalls += TemporalResolveRenderer.DrawCallsPerFrame;
        }

        // A frame without the resolve holds none of its targets. The history targets and the resolve's sets that name them
        // go to the retire queue, since the last frame's commands may still read them. The resolve keeps its pipelines for
        // the next time. The display targets drain and free.
        void ReleaseTemporalResolve()
        {
            TemporalHistory.ReleaseTargets(_retired);
            _temporalResolve?.ReleaseSets(_retired);
            _temporalPost?.Release();
        }

        /// <summary>From <c>Dispose</c>, after the device drained and the retire queue was disposed, so the history is
        /// released with no queue.</summary>
        void DisposeTemporalResolve()
        {
            _temporalPost?.Dispose();
            _temporalResolve?.Dispose();
            TemporalHistory.ReleaseTargets();
        }

        static TemporalViewInput ViewInput(in FrameView view) => new(view.View, view.Projection);
    }
}
