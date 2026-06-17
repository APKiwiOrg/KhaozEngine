using System;
using System.Numerics;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// A responsive <see cref="IDesignViewport"/>: the design <b>height</b> is fixed (so vertical layout and anchors
    /// stay constant) while the design <b>width</b> tracks the window's aspect ratio, with a uniform height-fit
    /// scale and <b>no letterbox</b>. The whole UI fills the window at any aspect instead of being pillarboxed;
    /// layout that is expressed relative to <see cref="Width"/>/<see cref="Height"/> (full-width bars, Width-relative
    /// grids, centered content) adapts automatically. Width never drops below the reference width, so a
    /// narrower-than-design window keeps the design's minimum rather than squishing.
    /// <para>
    /// Contrast with <see cref="DesignViewport"/>, which keeps a fixed reference size and letterboxes/pillarboxes
    /// to preserve it. Use <see cref="AdaptiveViewport"/> for a fixed-height design (e.g. mobile-portrait UI) that
    /// should fill a resizable desktop window edge-to-edge. Pure math (no window/GPU dependency) - headless-testable;
    /// drive it with <see cref="Update"/> each frame and pass it to <c>SpriteBatch.Begin(IDesignViewport)</c> /
    /// <c>Pointer.Update(InputState, IDesignViewport)</c> like any design viewport.
    /// </para>
    /// </summary>
    public sealed class AdaptiveViewport : IDesignViewport
    {
        readonly int _referenceWidth;

        /// <summary>Fixed design-space height (the layout's vertical reference).</summary>
        public int Height { get; }

        /// <summary>Design-space width; recomputed from the window aspect each <see cref="Update"/> (floored at the reference width).</summary>
        public int Width { get; private set; }

        public float ScaleX { get; private set; } = 1f;
        public float ScaleY { get; private set; } = 1f;

        /// <summary>Always 0 - the design fills the window width, so there is no horizontal letterbox.</summary>
        public float OffsetX => 0f;
        /// <summary>Always 0 - the design is height-fit, so there is no vertical letterbox.</summary>
        public float OffsetY => 0f;

        /// <summary>
        /// <paramref name="referenceWidth"/> is the design width at the design's own aspect (and the minimum width);
        /// <paramref name="referenceHeight"/> is the fixed design height.
        /// </summary>
        public AdaptiveViewport(int referenceWidth, int referenceHeight)
        {
            _referenceWidth = referenceWidth;
            Height = referenceHeight;
            Width = referenceWidth;
            Update(referenceWidth, referenceHeight);
        }

        /// <summary>Recompute the scale (fit to the fixed height) and the adaptive width from the window size. Ignores non-positive sizes.</summary>
        public void Update(int windowWidth, int windowHeight)
        {
            if (windowWidth <= 0 || windowHeight <= 0) return;
            float scale = windowHeight / (float)Height;
            ScaleX = ScaleY = scale;
            Width = Math.Max(_referenceWidth, (int)MathF.Round(windowWidth / scale));
        }

        /// <summary>Design rect covering the whole (current) design space: (0, 0, Width, Height).</summary>
        public Rect DesignBounds => new(0, 0, Width, Height);

        /// <summary>The window-pixel rect the design space is drawn into (the whole window; no letterbox bars).</summary>
        public Rect ContentBounds => new(0, 0, Width * ScaleX, Height * ScaleY);

        public Vector2 DesignToScreen(Vector2 design) => new(design.X * ScaleX, design.Y * ScaleY);
        public Vector2 ScreenToDesign(Vector2 screen) => new(screen.X / ScaleX, screen.Y / ScaleY);

        /// <summary>
        /// Design-coordinates-to-clip-space transform for <c>SpriteBatch.Begin</c>: the uniform scale folded into a
        /// y-down ortho (no letterbox offset). Mirrors <see cref="DesignViewport.GetClipProjection"/>.
        /// </summary>
        public Matrix4x4 GetClipProjection(int viewportWidth, int viewportHeight)
        {
            var design = Matrix4x4.CreateScale(ScaleX, ScaleY, 1f);
            var ortho = Matrix4x4.CreateOrthographicOffCenter(0, viewportWidth, viewportHeight, 0, -1, 1);
            return design * ortho;
        }
    }
}
