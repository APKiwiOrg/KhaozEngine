using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// A point-space viewport for DPI-aware UI: unlike <see cref="DesignViewport"/> (a fixed design canvas
    /// Fit-scaled onto the framebuffer, so it magnifies fractionally on HiDPI), this authors UI in <b>logical
    /// points</b> and maps 1 point to <see cref="DpiScale"/> device pixels. Its <see cref="Width"/>/<see cref="Height"/>
    /// track the logical window size (so the UI reflows as the window resizes, like a desktop app, rather than
    /// scaling), and its scale is the DPI factor - stable for a given display, changing only on a monitor / OS-scale
    /// change, not on resize. Bake fonts at that scale (see <c>DpiFont</c>) and draw 1:1 for crisp text; snap
    /// geometry to whole device pixels for pixel-exact chrome.
    /// <para>
    /// Implements <see cref="IDesignViewport"/>, so it drops straight into <c>SpriteBatch.Begin(IDesignViewport)</c>,
    /// <c>Pointer.Update(InputState, IDesignViewport)</c>, and the Gui screens/layout that already target that seam.
    /// Drive it each frame with <see cref="Update(Frame)"/> (or the explicit size overload). Pure math (no window or
    /// GPU dependency), so it is headless-testable.
    /// </para>
    /// </summary>
    public sealed class UiViewport : IDesignViewport
    {
        /// <summary>Logical width in points (the current logical window width).</summary>
        public int Width { get; private set; }
        /// <summary>Logical height in points (the current logical window height).</summary>
        public int Height { get; private set; }

        /// <summary>Device pixels per logical point (the DPI scale). Equals <see cref="ScaleX"/>/<see cref="ScaleY"/>.</summary>
        public float DpiScale { get; private set; } = 1f;

        public float ScaleX { get; private set; } = 1f;
        public float ScaleY { get; private set; } = 1f;

        /// <summary>Always 0: point-space UI is not letterboxed (it reflows to fill the window).</summary>
        public float OffsetX => 0f;
        /// <summary>Always 0: point-space UI is not letterboxed (it reflows to fill the window).</summary>
        public float OffsetY => 0f;

        /// <summary>True: point-space UI snaps to whole device pixels for crisp chrome (see <see cref="IDesignViewport.SnapsToDevicePixels"/>).</summary>
        public bool SnapsToDevicePixels => true;

        public UiViewport() { }

        /// <summary>Construct and size in one step; see <see cref="Update(int, int, int, int)"/>.</summary>
        public UiViewport(int framebufferWidth, int framebufferHeight, int logicalWidth, int logicalHeight)
            => Update(framebufferWidth, framebufferHeight, logicalWidth, logicalHeight);

        /// <summary>
        /// Recompute from the device framebuffer size (pixels) and the logical window size (points).
        /// <see cref="Width"/>/<see cref="Height"/> become the logical size; <see cref="DpiScale"/> (and both axis
        /// scales) become <c>framebufferWidth / logicalWidth</c>. Non-positive / degenerate input is ignored.
        /// </summary>
        public void Update(int framebufferWidth, int framebufferHeight, int logicalWidth, int logicalHeight)
        {
            if (framebufferWidth <= 0 || framebufferHeight <= 0 || logicalWidth <= 0 || logicalHeight <= 0) return;
            Width = logicalWidth;
            Height = logicalHeight;
            DpiScale = (float)framebufferWidth / logicalWidth;   // the OS scale factor (uniform on both axes)
            ScaleX = ScaleY = DpiScale;
        }

        /// <summary>Recompute from a <see cref="Frame"/> (its framebuffer <see cref="Frame.Width"/>/<see cref="Frame.Height"/>
        /// and logical <see cref="Frame.LogicalWidth"/>/<see cref="Frame.LogicalHeight"/>). The per-frame call a host makes.</summary>
        public void Update(Frame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            Update(frame.Width, frame.Height, frame.LogicalWidth, frame.LogicalHeight);
        }

        /// <summary>
        /// <see cref="IDesignViewport"/> form: interprets <paramref name="windowWidth"/>/<paramref name="windowHeight"/>
        /// as the device framebuffer and reuses the DPI scale set via the ctor / <see cref="Update(Frame)"/> (default 1),
        /// deriving the logical size back out. Prefer <see cref="Update(Frame)"/>, which carries the true logical size.
        /// </summary>
        public void Update(int windowWidth, int windowHeight)
        {
            if (windowWidth <= 0 || windowHeight <= 0 || DpiScale <= 0f) return;
            ScaleX = ScaleY = DpiScale;
            Width = (int)MathF.Round(windowWidth / DpiScale);
            Height = (int)MathF.Round(windowHeight / DpiScale);
        }

        /// <summary>Point-space rect covering the whole logical UI area: (0, 0, Width, Height).</summary>
        public Rect DesignBounds => new(0, 0, Width, Height);

        /// <summary>The device-pixel rect the UI covers: the full framebuffer (no letterbox), (0, 0, Width*scale, Height*scale).</summary>
        public Rect ContentBounds => new(0, 0, Width * ScaleX, Height * ScaleY);

        /// <summary>Equals <see cref="DesignBounds"/>: point-space UI reflows to fill the window, so there is no bar to cover.</summary>
        public Rect WindowBounds => DesignBounds;

        /// <summary>Map a logical point to device pixels (scale by DPI; no offset).</summary>
        public Vector2 DesignToScreen(Vector2 design) => new(design.X * ScaleX, design.Y * ScaleY);

        /// <summary>Map a device-pixel point back to logical points (for hit-testing the DPI-scaled cursor).</summary>
        public Vector2 ScreenToDesign(Vector2 screen) => new(screen.X / ScaleX, screen.Y / ScaleY);

        /// <summary>
        /// Point-to-clip transform for <c>SpriteBatch.Begin</c>: scale points up to device pixels then ortho by the
        /// framebuffer size, so a logical point at <c>p</c> lands at <c>p * DpiScale</c> device pixels and the logical
        /// extent fills the framebuffer. <paramref name="viewportWidth"/>/<paramref name="viewportHeight"/> are the
        /// framebuffer size the batch passes in.
        /// </summary>
        public Matrix4x4 GetClipProjection(int viewportWidth, int viewportHeight)
        {
            var toDevice = Matrix4x4.CreateScale(ScaleX, ScaleY, 1f);
            var ortho = Matrix4x4.CreateOrthographicOffCenter(0, viewportWidth, viewportHeight, 0, -1, 1);
            return toDevice * ortho;
        }
    }
}
