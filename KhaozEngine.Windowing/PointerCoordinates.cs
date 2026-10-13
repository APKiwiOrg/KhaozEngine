using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Windowing;

/// <summary>
/// Immutable affine mapping into Pointer coordinates. It snapshots mutable viewport values alongside identity
/// and framebuffer scale so a controller can detect mapping changes without retaining a live transform.
/// </summary>
public readonly record struct PointerCoordinates
{
    readonly Vector2 _framebufferScale;
    public Vector2 FramebufferScale => PointerMotionScale.Normalize(_framebufferScale);
    public IDesignViewport? Viewport { get; }
    public Vector2 ViewportScale { get; }
    public Vector2 ViewportOffset { get; }
    public int ViewportWidth { get; }
    public int ViewportHeight { get; }
    public int FramebufferWidth { get; }
    public int FramebufferHeight { get; }

    public PointerCoordinates(Vector2 framebufferScale, IDesignViewport? viewport = null,
        int framebufferWidth = 0, int framebufferHeight = 0)
    {
        _framebufferScale = PointerMotionScale.Normalize(framebufferScale);
        Viewport = viewport;
        ViewportScale = viewport == null ? Vector2.One : new Vector2(viewport.ScaleX, viewport.ScaleY);
        ViewportOffset = viewport == null ? Vector2.Zero : new Vector2(viewport.OffsetX, viewport.OffsetY);
        ViewportWidth = viewport?.Width ?? 0;
        ViewportHeight = viewport?.Height ?? 0;
        FramebufferWidth = framebufferWidth;
        FramebufferHeight = framebufferHeight;
    }

    /// <summary>Map framebuffer positions, including event anchors and touch contacts.</summary>
    public Vector2 PositionFromPixels(Vector2 position) =>
        Viewport == null ? position : (position - ViewportOffset) / ViewportScale;

    /// <summary>Map framebuffer vectors without applying viewport translation.</summary>
    public Vector2 VectorFromPixels(Vector2 delta) => Viewport == null ? delta : delta / ViewportScale;

    /// <summary>Map window-point vectors, applying framebuffer scale exactly once.</summary>
    public Vector2 VectorFromPoints(Vector2 delta) => VectorFromPixels(delta * FramebufferScale);

    /// <summary>Compare viewport identity and its captured transform, scale and dimensions.</summary>
    public bool HasSameMapping(PointerCoordinates other) => ReferenceEquals(Viewport, other.Viewport)
        && FramebufferScale == other.FramebufferScale && ViewportScale == other.ViewportScale
        && ViewportOffset == other.ViewportOffset && ViewportWidth == other.ViewportWidth
        && ViewportHeight == other.ViewportHeight && FramebufferWidth == other.FramebufferWidth
        && FramebufferHeight == other.FramebufferHeight;
}
