using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing
{
    /// <summary>One render frame: timing, the input snapshot, and the GPU command list to draw into
    /// (the swapchain is already bound and cleared by <see cref="AppWindow"/>).</summary>
    public sealed class Frame
    {
        float _contentScale;

        public float Dt { get; internal set; }
        public InputState Input { get; internal set; } = InputState.Empty;
        /// <summary>Render (framebuffer) size in device pixels - the swapchain resolution the 2D/3D renderers draw
        /// at (2x the logical size on Retina, etc.). This is what <c>SpriteBatch</c> and <c>DesignViewport</c> map into.</summary>
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        /// <summary>Logical window size in points (device framebuffer / <see cref="DpiScale"/>, rounded to the nearest
        /// point, so the logical extent can differ from the framebuffer edge by at most half a logical point). UI
        /// authored in points scales to device pixels by <see cref="DpiScale"/>. Drive a <c>UiViewport</c> from this
        /// frame so text and chrome stay crisp.</summary>
        public int LogicalWidth { get; internal set; }
        public int LogicalHeight { get; internal set; }
        /// <summary>Device pixels per logical point: the window's OS content scale, 1 on a standard display, 2 on
        /// Retina, 1.5 on a 150%-scaled Windows display even though its window coordinates are pixels. It stays exact
        /// across resizes and changes on a monitor move or OS scale change. Bake point-space UI fonts at this scale
        /// (<c>DpiFont.For(frame.DpiScale)</c>) and snap UI geometry to whole multiples of it. Without an OS scale
        /// (a backend without the query, or a manually built frame) it is <see cref="Width"/> /
        /// <see cref="LogicalWidth"/>, and 1 before the logical size is known.</summary>
        public float DpiScale => _contentScale > 0f ? _contentScale : LogicalWidth > 0 ? (float)Width / LogicalWidth : 1f;
        /// <summary>The engine GPU command list for this frame (the swapchain is already bound and cleared;
        /// renderers draw into it). Backend GPU types stay hidden behind <see cref="IGpuCommandList"/>.</summary>
        public IGpuCommandList Commands { get; internal set; } = null!;
        /// <summary>True when the loop is suppressing render + present for this frame (the window is minimized under
        /// the background-throttle policy): the swapchain was NOT begun/cleared and will NOT be presented, so a
        /// callback must NOT draw into <see cref="Commands"/> this frame - run update-only. Update still runs each
        /// suppressed frame so simulation/netcode/timers keep advancing while iconified. Always false while the
        /// window is visible. <c>GameApp</c> honours this automatically.</summary>
        public bool RenderSuppressed { get; internal set; }

        /// <summary>Latch this frame's sizes. The exact OS content scale becomes <see cref="DpiScale"/> and the
        /// logical size is the framebuffer divided by it. An unusable scale keeps the window-coordinate size and the
        /// framebuffer-to-window fallback.</summary>
        internal void SetMetrics(int framebufferWidth, int framebufferHeight, int windowWidth, int windowHeight,
            float osContentScale)
        {
            var logical = DisplayScale.Logical(framebufferWidth, framebufferHeight, windowWidth, windowHeight,
                osContentScale);
            Width = framebufferWidth; Height = framebufferHeight;
            LogicalWidth = logical.Width; LogicalHeight = logical.Height;
            _contentScale = logical.ContentScale;
        }
    }
}
