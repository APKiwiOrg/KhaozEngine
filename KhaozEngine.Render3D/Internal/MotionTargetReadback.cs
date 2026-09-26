using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>The motion target read back for a test: <paramref name="Motion"/> holds one UV motion per internal pixel,
/// row-major with row 0 at the top.</summary>
internal readonly record struct MotionTargetReadback(Vector2[] Motion, int Width, int Height)
{
    /// <summary>The stored UV motion at one pixel.</summary>
    public Vector2 UvAt(int x, int y) => Motion[y * Width + x];

    /// <summary>The motion at one pixel in internal pixels, x right and y down.</summary>
    public Vector2 PixelsAt(int x, int y) => UvAt(x, y) * new Vector2(Width, Height);

    /// <summary>Whether no opaque geometry drew at this pixel.</summary>
    public bool IsBackground(int x, int y) => MotionMath.IsBackground(UvAt(x, y));
}
