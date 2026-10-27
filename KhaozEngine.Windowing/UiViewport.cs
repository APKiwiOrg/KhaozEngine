using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// A point-space viewport for DPI-aware UI. One design point maps to <see cref="EffectiveScale"/> device
    /// pixels, combining the physical <see cref="DpiScale"/> with the requested <see cref="ScaleMultiplier"/>.
    /// The canvas reflows to fill the framebuffer without letterboxing. Bake fonts at the effective scale
    /// (see <c>DpiFont</c>) and draw 1:1 for crisp text. Geometry snaps to device pixels.
    /// <para>
    /// Implements <see cref="IDesignViewport"/>, so it drops straight into <c>SpriteBatch.Begin(IDesignViewport)</c>,
    /// <c>Pointer.Update(InputState, IDesignViewport)</c>, and the Gui screens/layout that already target that seam.
    /// Drive it each frame with <see cref="Update(Frame)"/> (or the explicit size overload). Pure math (no window or
    /// GPU dependency), so it is headless-testable.
    /// </para>
    /// </summary>
    public sealed class UiViewport : IDesignViewport
    {
        int _framebufferWidth, _framebufferHeight;
        float _requestedScale = 1f;

        /// <summary>The UI canvas width, rounded to whole design points for integer layout consumers.</summary>
        public int Width { get; private set; }
        /// <summary>The UI canvas height, rounded to whole design points for integer layout consumers.</summary>
        public int Height { get; private set; }

        /// <summary>Physical device pixels per OS logical point, independent of user UI scale.</summary>
        public float DpiScale { get; private set; } = 1f;

        /// <summary>
        /// Requested user UI multiplier. The next valid Update applies it to layout, projection and pointer
        /// mapping together. Setting it while a frame is being handled never remaps that frame's input.
        /// Values must be finite and between 0.1 and 10 inclusive. Consumers may offer a narrower range.
        /// </summary>
        public float ScaleMultiplier
        {
            get => _requestedScale;
            set
            {
                if (!float.IsFinite(value) || value < 0.1f || value > 10f)
                    throw new ArgumentOutOfRangeException(nameof(value), value,
                        "UI scale must be finite and between 0.1 and 10.");
                _requestedScale = value;
            }
        }

        /// <summary>Applied device pixels per UI design point. Use this density for UI font rasterization.</summary>
        public float EffectiveScale => ScaleX;

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
        /// The physical DPI is <c>framebufferWidth / logicalWidth</c>. The requested user scale is applied
        /// to the UI canvas and both axis scales. Non-positive or degenerate input is ignored.
        /// </summary>
        public void Update(int framebufferWidth, int framebufferHeight, int logicalWidth, int logicalHeight)
        {
            if (framebufferWidth <= 0 || framebufferHeight <= 0 || logicalWidth <= 0 || logicalHeight <= 0) return;
            Apply(framebufferWidth, framebufferHeight, logicalWidth, logicalHeight,
                (float)framebufferWidth / logicalWidth);
        }

        /// <summary>Recompute from a <see cref="Frame"/>: its logical <see cref="Frame.LogicalWidth"/>/<see cref="Frame.LogicalHeight"/>
        /// and its exact <see cref="Frame.DpiScale"/>, so rounding the logical size on a resize never moves the scale.
        /// The per-frame call a host makes. Frames with a non-positive framebuffer or logical size are ignored.</summary>
        public void Update(Frame frame)
        {
            ArgumentNullException.ThrowIfNull(frame);
            if (frame.Width <= 0 || frame.Height <= 0 || frame.LogicalWidth <= 0 || frame.LogicalHeight <= 0) return;
            Apply(frame.Width, frame.Height, frame.LogicalWidth, frame.LogicalHeight, frame.DpiScale);
        }

        /// <summary>
        /// <see cref="IDesignViewport"/> form: interprets <paramref name="windowWidth"/>/<paramref name="windowHeight"/>
        /// as the device framebuffer and reuses the DPI scale set via the ctor / <see cref="Update(Frame)"/> (default 1),
        /// deriving the logical size back out. Prefer <see cref="Update(Frame)"/>, which carries the true logical size.
        /// </summary>
        public void Update(int windowWidth, int windowHeight)
        {
            if (windowWidth <= 0 || windowHeight <= 0 || DpiScale <= 0f) return;
            Apply(windowWidth, windowHeight, windowWidth / (double)DpiScale,
                windowHeight / (double)DpiScale, DpiScale);
        }

        void Apply(int framebufferWidth, int framebufferHeight, double logicalWidth, double logicalHeight,
            float dpiScale)
        {
            float effective = dpiScale * _requestedScale;
            if (!float.IsFinite(effective) || effective <= 0f) return;
            int width = (int)Math.Clamp(Math.Round(logicalWidth / _requestedScale), 1d, int.MaxValue);
            int height = (int)Math.Clamp(Math.Round(logicalHeight / _requestedScale), 1d, int.MaxValue);
            _framebufferWidth = framebufferWidth;
            _framebufferHeight = framebufferHeight;
            Width = width;
            Height = height;
            DpiScale = dpiScale;
            ScaleX = ScaleY = effective;
        }

        /// <summary>Exact UI canvas coverage, including fractional points lost by integer layout rounding.</summary>
        public Rect DesignBounds => new(0, 0, _framebufferWidth / ScaleX, _framebufferHeight / ScaleY);

        /// <summary>The complete device framebuffer, without letterbox margins or logical-size rounding.</summary>
        public Rect ContentBounds => new(0, 0, _framebufferWidth, _framebufferHeight);

        /// <summary>Equals <see cref="DesignBounds"/>: point-space UI reflows to fill the window, so there is no bar to cover.</summary>
        public Rect WindowBounds => DesignBounds;

        /// <summary>Map a UI design point to device pixels using the applied effective scale, without offset.</summary>
        public Vector2 DesignToScreen(Vector2 design) => new(design.X * ScaleX, design.Y * ScaleY);

        /// <summary>Map a device-pixel point back into the same UI space used by drawing and clipping.</summary>
        public Vector2 ScreenToDesign(Vector2 screen) => new(screen.X / ScaleX, screen.Y / ScaleY);

        /// <summary>
        /// Point-to-clip transform for <c>SpriteBatch.Begin</c>: scale points up to device pixels then ortho by the
        /// framebuffer size, so a logical point at <c>p</c> lands at <c>p * EffectiveScale</c> device pixels and the logical
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
