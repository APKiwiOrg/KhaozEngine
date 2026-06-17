using System.Numerics;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// The fakeable design-viewport seam that rendering and layout target: design size, per-axis scale +
    /// letterbox offset, and screen&lt;-&gt;design mapping. <see cref="DesignViewport"/> is the concrete
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

        /// <summary>Design rect covering the whole design space.</summary>
        Rect DesignBounds { get; }
        /// <summary>Window-pixel rect the design space is drawn into (excludes letterbox bars).</summary>
        Rect ContentBounds { get; }

        /// <summary>Map a design-space point to window pixels.</summary>
        Vector2 DesignToScreen(Vector2 design);
        /// <summary>Map a window-pixel point to design space (for hit-testing).</summary>
        Vector2 ScreenToDesign(Vector2 screen);

        /// <summary>Design-to-clip transform for <c>SpriteBatch.Begin</c> at the given window size (points).</summary>
        Matrix4x4 GetClipProjection(int viewportWidth, int viewportHeight);
    }
}
