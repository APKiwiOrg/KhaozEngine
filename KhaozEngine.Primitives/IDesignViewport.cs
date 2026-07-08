using System.Numerics;

namespace KhaozEngine.Primitives
{
    /// <summary>
    /// The fakeable design-viewport seam that rendering and layout target: design size, per-axis scale +
    /// letterbox offset, and screen&lt;-&gt;design mapping. <c>DesignViewport</c> (KhaozEngine.Windowing) is the concrete
    /// implementation; the interface lets headless tests and layout code work against a small stub instead of
    /// a live window. (Custom-stack analogue of the 4.x MonoGame <c>IDesignViewport</c>, on System.Numerics.)
    /// </summary>
    public interface IDesignViewport
    {
        /// <summary>Recompute the scale/letterbox from the current window size (points). Called once per frame.</summary>
        void Update(int windowWidth, int windowHeight);

        /// <summary>Design-space width.</summary>
        int Width { get; }
        /// <summary>Design-space height.</summary>
        int Height { get; }
        /// <summary>Horizontal scale from design to window pixels.</summary>
        float ScaleX { get; }
        /// <summary>Vertical scale from design to window pixels.</summary>
        float ScaleY { get; }
        /// <summary>Horizontal letterbox offset in window pixels.</summary>
        float OffsetX { get; }
        /// <summary>Vertical letterbox offset in window pixels.</summary>
        float OffsetY { get; }

        /// <summary>
        /// Whether drawing through this viewport should snap geometry to whole device pixels. True for a point-space
        /// UI viewport, where 1 unit maps to an integer-friendly DPI scale and snapping yields crisp 1px chrome;
        /// false (the default) for a fractional design canvas, where snapping would fight the intended smooth
        /// scaling of the game field. Lets <c>SpriteBatch</c> confine device-pixel snapping to the point-space path.
        /// </summary>
        bool SnapsToDevicePixels => false;

        /// <summary>Design rect covering the whole design space.</summary>
        Rect DesignBounds { get; }
        /// <summary>Window-pixel rect the design space is drawn into (excludes letterbox bars).</summary>
        Rect ContentBounds { get; }

        /// <summary>
        /// The whole window mapped back into design space: <see cref="DesignBounds"/> plus the letterbox/pillarbox
        /// bars around it. Under a fit-style scale the design rect sits inset with bars, so a fullscreen fill sized
        /// from <see cref="Width"/>/<see cref="Height"/> stops at the design edge and the window bars show the screen
        /// below; fill this rect instead to cover the whole window (its origin is negative and its size exceeds the
        /// design when there is a bar). Reduces exactly to <see cref="DesignBounds"/> when there is no letterbox
        /// (zero offset). Derived from the scale + offset, so every implementer gets it for free; a viewport with an
        /// asymmetric letterbox must override it.
        /// </summary>
        Rect WindowBounds =>
            new(-OffsetX / ScaleX, -OffsetY / ScaleY,
                Width + 2f * OffsetX / ScaleX, Height + 2f * OffsetY / ScaleY);

        /// <summary>Map a design-space point to window pixels.</summary>
        Vector2 DesignToScreen(Vector2 design);
        /// <summary>Map a window-pixel point to design space (for hit-testing).</summary>
        Vector2 ScreenToDesign(Vector2 screen);

        /// <summary>Design-to-clip transform for <c>SpriteBatch.Begin</c> at the given window size (points).</summary>
        Matrix4x4 GetClipProjection(int viewportWidth, int viewportHeight);
    }
}
