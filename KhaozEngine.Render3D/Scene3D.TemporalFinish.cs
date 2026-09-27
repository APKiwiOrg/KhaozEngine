using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The finishing layer over the temporal resolve (docs/design/TEMPORAL-RESOLVE-UPSCALING-DESIGN-2026-09-24.md,
    /// sections 4 and 5, amendments 7 and 20): the sharpen's test seams and the material mip bias. The sharpen runs in
    /// the display post chain, which only the first render of a resolving frame runs, so a later render's chain and a
    /// frame without the resolve never build it. The mip bias follows the same rule. A partial of its own because
    /// <c>Scene3D.cs</c> is frozen by the file-size ratchet.
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
        // leaves them alone, as it leaves the frame's other diagnostics alone.
        int _temporalDisplayWidth, _temporalDisplayHeight, _temporalInternalWidth, _temporalInternalHeight;
        long _temporalSizesFrame = -1;

        /// <summary>
        /// This render's material mip bias for the frame block's <c>Params.z</c> and <c>Params.w</c>
        /// (<see cref="TemporalMipBias"/>). Called from the model pass's frame upload in <c>RenderInternal</c>, after
        /// the frame view is latched. Only the render the temporal resolve accumulates takes a bias: the first render
        /// of a resolving frame. A later render inside that frame is unjittered and unresolved, so nothing absorbs a
        /// negative bias there, and it gets exact zeros, as does every render of a frame that does not resolve. The
        /// scale is the frame's latched display over internal scale, the unrounded one with the render cap included
        /// that also set the frame's jitter cycle. On the frame's first render this also latches the sizes the
        /// diagnostics report.
        /// </summary>
        Vector2 MaterialLod()
        {
            if (_currentFrameView.FrameIndex != _temporalSizesFrame)
            {
                _temporalSizesFrame = _currentFrameView.FrameIndex;
                _temporalDisplayWidth = _latchedDisplayWidth;
                _temporalDisplayHeight = _latchedDisplayHeight;
                _temporalInternalWidth = _currentFrameView.Width;
                _temporalInternalHeight = _currentFrameView.Height;
            }
            return TemporalMipBias.For(_resolveThisRender, _frameDisplayOverInternal, Post.Temporal.MipBiasOffset);
        }

        /// <summary>The model pass's packed frame block as last uploaded. Internal, for the tests.</summary>
        internal ReadOnlySpan<byte> FrameImageForTests => _model.FrameImage;
    }
}
