using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// THE CLIP REGION: the design-space to framebuffer-pixel mapping, and the stack that makes nested clips
    /// compose. Split out of <c>SpriteBatch.cs</c> for
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/106">#106</see>, because clipping is one
    /// coherent thing rather than an arbitrary slice of the batch (and that file sits at its frozen size).
    /// </summary>
    public sealed partial class SpriteBatch
    {
        // Active clips, innermost last, already in framebuffer pixels. Empty means no clip: the full framebuffer.
        // Pixels rather than the caller's rects because the mapping depends on the viewport that was active when
        // each clip was pushed, so an outer clip pushed under one pass still bounds an inner clip pushed under
        // another. Cleared at NewFrame so a frame that threw mid-clip cannot leak its region into the next one.
        readonly List<(uint X, uint Y, uint Width, uint Height)> _scissorStack = new();

        /// <summary>How many clip regions are currently active (0 = unclipped). One per unmatched <see cref="SetScissor"/>.</summary>
        public int ScissorDepth => _scissorStack.Count;

        /// <summary>
        /// Convert a clip rect (in viewport points, top-left origin) to framebuffer pixels, scaling for DPI
        /// (e.g. 2x Retina) and clamping to the framebuffer. Pure function, unit-tested headlessly.
        /// </summary>
        public static (uint X, uint Y, uint Width, uint Height) ComputeScissor(
            Rect rect, int viewportW, int viewportH, int framebufferW, int framebufferH)
        {
            float sx = viewportW > 0 ? (float)framebufferW / viewportW : 1f;
            float sy = viewportH > 0 ? (float)framebufferH / viewportH : 1f;
            float x0 = Math.Clamp(rect.X * sx, 0, framebufferW);
            float x1 = Math.Clamp((rect.X + rect.Width) * sx, 0, framebufferW);
            float y0 = Math.Clamp(rect.Y * sy, 0, framebufferH);
            float y1 = Math.Clamp((rect.Y + rect.Height) * sy, 0, framebufferH);
            return ((uint)MathF.Round(x0), (uint)MathF.Round(y0),
                    (uint)MathF.Round(x1 - x0), (uint)MathF.Round(y1 - y0));
        }

        /// <summary>
        /// As <see cref="ComputeScissor(Rect,int,int,int,int)"/>, but first maps a clip rect given in
        /// design space through <paramref name="viewport"/> (scale + letterbox offset) into window points. Pass
        /// a null viewport to treat <paramref name="rect"/> as already in window points. Pure / headless.
        /// </summary>
        public static (uint X, uint Y, uint Width, uint Height) ComputeScissor(
            Rect rect, IDesignViewport? viewport, int viewportW, int viewportH, int framebufferW, int framebufferH)
        {
            if (viewport != null)
            {
                var tl = viewport.DesignToScreen(new Vector2(rect.X, rect.Y));
                var br = viewport.DesignToScreen(new Vector2(rect.Right, rect.Bottom));
                rect = new Rect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
            }
            return ComputeScissor(rect, viewportW, viewportH, framebufferW, framebufferH);
        }

        /// <summary>
        /// The overlap of two scissor rects in framebuffer pixels, which is what a clip nested inside another one
        /// is allowed to draw in. A pair that does not overlap gives a zero-sized region (everything clipped),
        /// never a negative one. Pure / headless.
        /// </summary>
        public static (uint X, uint Y, uint Width, uint Height) IntersectScissor(
            (uint X, uint Y, uint Width, uint Height) outer, (uint X, uint Y, uint Width, uint Height) inner)
        {
            uint x0 = Math.Max(outer.X, inner.X);
            uint y0 = Math.Max(outer.Y, inner.Y);
            uint x1 = Math.Min(outer.X + outer.Width, inner.X + inner.Width);
            uint y1 = Math.Min(outer.Y + outer.Height, inner.Y + inner.Height);
            return (x0, y0, x1 > x0 ? x1 - x0 : 0u, y1 > y0 ? y1 - y0 : 0u);
        }

        /// <summary>
        /// Flush pending draws, then clip subsequent draws to <paramref name="rect"/>. When a design viewport is
        /// active (<see cref="Begin(IDesignViewport, SamplerMode)"/>) <paramref name="rect"/> is in design space and
        /// is mapped through it; otherwise it is in window points. Pair with <see cref="ClearScissor"/>. The
        /// current transform is preserved, so no <see cref="Begin(Camera2D, SamplerMode)"/> is needed around it.
        /// <para>Clips NEST: called while another clip is active, the effective region is the overlap of the two,
        /// and the matching <see cref="ClearScissor"/> restores the outer one rather than the whole framebuffer.
        /// A widget that clips (ScrollablePanel, PopupPanel, PropertyGrid, TreeView, PannableCanvas,
        /// PatchNotesView, the text fields) therefore composes with any other one it is drawn inside.</para>
        /// </summary>
        public void SetScissor(Rect rect)
        {
            Flush();
            var (fbw, fbh) = FramebufferSize();
            var region = ComputeScissor(rect, _viewport, _vw, _vh, (int)fbw, (int)fbh);
            if (_scissorStack.Count > 0) region = IntersectScissor(_scissorStack[^1], region);
            _scissorStack.Add(region);
            _cl.SetScissorRect(0, region.X, region.Y, region.Width, region.Height);
        }

        /// <summary>
        /// Flush pending (clipped) draws, then undo the innermost <see cref="SetScissor"/>: back to the clip that
        /// was active around it, or to the full framebuffer when that was the only one. Unpaired (nothing to pop)
        /// it resets to the full framebuffer, which is what it always did.
        /// </summary>
        public void ClearScissor()
        {
            Flush();
            if (_scissorStack.Count > 0) _scissorStack.RemoveAt(_scissorStack.Count - 1);
            var (fbw, fbh) = FramebufferSize();
            var region = _scissorStack.Count > 0 ? _scissorStack[^1] : (X: 0u, Y: 0u, Width: fbw, Height: fbh);
            _cl.SetScissorRect(0, region.X, region.Y, region.Width, region.Height);
        }

        // The swapchain framebuffer's pixel size, falling back to the viewport when there is no swapchain (an
        // offscreen capture). Both scissor entry points need it and neither owns it.
        (uint Width, uint Height) FramebufferSize()
        {
            var fb = _gd.SwapchainFramebuffer;
            return fb != null ? (fb.Width, fb.Height) : ((uint)Math.Max(0, _vw), (uint)Math.Max(0, _vh));
        }
    }
}
