using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The finishing layer over the temporal resolve (docs/design/TEMPORAL-RESOLVE-UPSCALING-DESIGN-2026-09-24.md,
    /// sections 4 to 6, amendments 7, 8 and 20): the sharpen's test seams, the material mip bias, the resolve's debug
    /// views, the sizes, preset and ratio the diagnostics report, and the counts they report on request. The sharpen
    /// runs in the display post chain, which only the first render of a resolving frame runs, so a later render's
    /// chain and a frame without the resolve never build it. The mip bias, the debug views and the counts follow the
    /// same rule, and the diagnostics describe that first render. A partial of its own because <c>Scene3D.cs</c> is
    /// frozen by the file-size ratchet.
    /// </summary>
    public sealed partial class Scene3D
    {
        /// <summary>Whether the post chain has built the temporal sharpen. Internal, for the tests that pin that a
        /// frame without temporal sharpening builds nothing.</summary>
        internal bool TemporalSharpenBuiltForTests => _post.SharpenBuilt;

        /// <summary>Whether the later renders' post chain has built the temporal sharpen, which it never should.
        /// Internal, for the tests.</summary>
        internal bool LaterRenderSharpenBuiltForTests => _laterRenderPost?.SharpenBuilt == true;

        /// <summary>The post chain's temporal sharpen, null until it is built. Internal, for the tests that pin that it
        /// follows the display targets whenever they are rebuilt.</summary>
        internal TemporalSharpenPass? TemporalSharpenForTests => _post.SharpenForTests;

        // The internal and display sizes of the frame's first render, latched by MaterialLod, which every
        // render calls before it uploads the frame block. The diagnostics report them. A later render inside the frame
        // leaves them alone, as it leaves the frame's other diagnostics alone. All four are zero for a frame whose
        // first render had no display area, whose display size would be the last nonzero one (_latchedDisplayWidth)
        // beside whatever internal target such a render allocates: a pixel or two where the internal size follows the
        // viewport, the unchanged fixed size under RenderScale.FixedInternal.
        int _temporalDisplayWidth, _temporalDisplayHeight, _temporalInternalWidth, _temporalInternalHeight;
        long _temporalSizesFrame = -1;
        // The upscale preset the frame's first render read, latched with the sizes, so a later render or a settings
        // change after the frame leaves the reported preset alone.
        TemporalUpscale _temporalPreset;
        // Whether the frame's first render ran the temporal resolve, latched with the sizes. The reported ratio is the
        // resolve's upscale, so a frame the resolve did not run on reports 1.
        bool _temporalResolved;

        // The latest on-request counts, filled when a requested frame's grid is harvested. The frame is minus 1 and
        // the counts zero until the first harvest.
        long _countsFrame = -1;
        int _countsDisoccluded, _countsReactive, _countsClipped;

        // A request waiting for a resolving render, and a grid recorded on the armed frame and not yet harvested, with
        // that frame's index and display size.
        bool _temporalCountsRequested, _temporalCountsArmed;
        long _countsArmedFrame;
        int _countsDisplayWidth, _countsDisplayHeight;

        /// <summary>
        /// This render's material mip bias for the frame block's <c>Params.z</c> and <c>Params.w</c>
        /// (<see cref="TemporalMipBias"/>). Called from the model pass's frame upload in <c>RenderInternal</c>, after
        /// the frame view is latched. Only the render the temporal resolve accumulates takes a bias: the first render
        /// of a resolving frame. A later render inside that frame is unjittered and unresolved, so nothing absorbs a
        /// negative bias there, and it gets exact zeros, as does every render of a frame that does not resolve. The
        /// scale is the frame's latched display over internal scale, the unrounded one with the render cap included
        /// that also set the frame's jitter cycle. On the frame's first render this also latches the sizes and the
        /// preset the diagnostics report. On the render that runs the resolve it arms a pending count request, before
        /// the resolve records.
        /// </summary>
        Vector2 MaterialLod()
        {
            if (_currentFrameView.FrameIndex != _temporalSizesFrame)
            {
                _temporalSizesFrame = _currentFrameView.FrameIndex;
                bool shown = _frameHasDisplayArea;
                _temporalDisplayWidth = shown ? _latchedDisplayWidth : 0;
                _temporalDisplayHeight = shown ? _latchedDisplayHeight : 0;
                _temporalInternalWidth = shown ? _currentFrameView.Width : 0;
                _temporalInternalHeight = shown ? _currentFrameView.Height : 0;
                _temporalPreset = Post.Temporal.Upscale;
                _temporalResolved = _resolveThisRender;
            }
            if (_resolveThisRender) ArmTemporalCounts(_latchedDisplayWidth, _latchedDisplayHeight);
            return TemporalMipBias.For(_resolveThisRender, _frameDisplayOverInternal, Post.Temporal.MipBiasOffset);
        }

        /// <summary>
        /// Sample the temporal counts (<see cref="TemporalDiagnostics.DisoccludedPixels"/>,
        /// <see cref="TemporalDiagnostics.ReactivePixels"/>, <see cref="TemporalDiagnostics.ClippedPixels"/>) on the
        /// next frame the temporal resolve runs. They land in <see cref="LastTemporalDiagnostics"/> at the following
        /// <see cref="PrepareFrame"/>, with <see cref="TemporalDiagnostics.CountsFrameIndex"/> naming the frame. The
        /// counts are estimates from a 32 by 18 grid of 16 samples a cell. Reading them back drains the device once
        /// on Metal and Vulkan, so request them from a debug overlay a few times a second at most, never every frame.
        /// <para>
        /// The request waits for the first render of a frame that runs the resolve and has a display area. A frame
        /// under another anti-aliasing mode, a later render inside a frame, such as an offscreen capture, and a
        /// zero-size render leave it pending. Requesting again before it is sampled changes nothing. A frame with no
        /// request records, reads and allocates nothing for the counts.
        /// </para>
        /// </summary>
        public void RequestTemporalCounts() => _temporalCountsRequested = true;

        // Called from MaterialLod on the render that runs the resolve, before the resolve records. A zero-size render
        // keeps the last nonzero display size, which would scale the counts of a 1 by 1 resolve, so it waits.
        void ArmTemporalCounts(int displayWidth, int displayHeight)
        {
            if (!_temporalCountsRequested || !_frameHasDisplayArea || _temporalResolve is not { } resolve) return;
            resolve.ArmCounts();
            _temporalCountsRequested = false;
            _temporalCountsArmed = true;
            _countsArmedFrame = _currentFrameView.FrameIndex;
            _countsDisplayWidth = displayWidth;
            _countsDisplayHeight = displayHeight;
        }

        // Called from PrepareFrame, where no list is recording: harvest the armed frame once, scaled to pixels. A frame
        // that armed nothing returns at the first test, so it maps and allocates nothing.
        void HarvestTemporalCounts()
        {
            if (!_temporalCountsArmed || _temporalResolve is not { } resolve) return;
            _temporalCountsArmed = false;
            if (!resolve.TryHarvestCounts(out int disoccluded, out int reactive, out int clipped)) return;
            double scale = (double)_countsDisplayWidth * _countsDisplayHeight / TemporalCountProbe.TotalSamples;
            _countsFrame = _countsArmedFrame;
            _countsDisoccluded = (int)Math.Round(disoccluded * scale);
            _countsReactive = (int)Math.Round(reactive * scale);
            _countsClipped = (int)Math.Round(clipped * scale);
        }

        /// <summary>The PrepareFrame harvest, for tests that drained the device already.</summary>
        internal void CollectTemporalCountsForTests() => HarvestTemporalCounts();

        /// <summary>Whether the resolve has built its count probe. Internal, for the tests.</summary>
        internal bool TemporalCountProbeBuiltForTests => _temporalResolve?.CountProbeBuilt ?? false;

        /// <summary>How many times the count probe's grid has been read back. Internal, for the tests.</summary>
        internal int TemporalCountReadbacksForTests => _temporalResolve?.CountReadbacks ?? 0;

        /// <summary>The diagnostics <see cref="AdvanceTemporalHistory"/> published, with the frame's sizes, preset and
        /// ratio and the latest counts composed in. The ratio is the internal to display width ratio of a frame the
        /// resolve ran on, and 1 on any other. A struct copy, so reading <see cref="LastTemporalDiagnostics"/> still
        /// allocates nothing. Before the first render it is the published value unchanged, the default, which
        /// <c>TemporalDiagnosticsTests.BeforeTheFirstRenderTheDiagnosticsAreDefault</c> pins.</summary>
        TemporalDiagnostics WithSizesAndCounts(in TemporalDiagnostics published) => _temporalSizesFrame < 0
            ? published
            : published with
            {
                InternalWidth = _temporalInternalWidth,
                InternalHeight = _temporalInternalHeight,
                DisplayWidth = _temporalDisplayWidth,
                DisplayHeight = _temporalDisplayHeight,
                Preset = _temporalPreset,
                UpscaleRatio = _temporalResolved && _temporalDisplayWidth > 0
                    ? (float)_temporalInternalWidth / _temporalDisplayWidth
                    : 1f,
                CountsFrameIndex = _countsFrame,
                DisoccludedPixels = _countsDisoccluded,
                ReactivePixels = _countsReactive,
                ClippedPixels = _countsClipped,
            };

        /// <summary>The model pass's packed frame block as last uploaded. Internal, for the tests.</summary>
        internal ReadOnlySpan<byte> FrameImageForTests => _model.FrameImage;

        /// <summary>
        /// Draw the frame's History, Disocclusion or Reactive view over <paramref name="target"/>, re-evaluating the
        /// resolve this render ran over the set and uniforms it used. Only the render that ran the resolve draws it: a
        /// frame without temporal anti-aliasing draws nothing, and so does a later render of a resolving frame, which
        /// is unjittered and unresolved and would re-evaluate the first render's inputs against its own image. The
        /// resolve, the sharpen and the mip bias run exactly as they do without a view, and the view only replaces the
        /// final image after them.
        /// </summary>
        void DrawTemporalDebugView(IGpuCommandList cl, IGpuFramebuffer target)
        {
            if (!_resolveThisRender || _temporalResolve is not { } resolve) return;
            resolve.DrawDebugView(cl, _frameDebugView, target, _targetOutput);
        }

        /// <summary>Whether the resolve has built its debug view pass. Internal, for the tests.</summary>
        internal bool TemporalDebugViewBuiltForTests => _temporalResolve?.DebugViewBuilt ?? false;
    }
}
