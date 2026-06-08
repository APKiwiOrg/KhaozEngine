using Microsoft.Xna.Framework;

namespace KhaozEngine.Input;

/// <summary>
/// Wraps an arbitrary screen-to-virtual <see cref="Matrix"/> (for example a game's existing
/// input-transform matrix), with optional clamp bounds.
/// </summary>
public sealed class MatrixTransform : ICoordinateTransform
{
    private Matrix _matrix;

    /// <summary>Creates the transform from a matrix and optional clamp bounds.</summary>
    public MatrixTransform(Matrix matrix, Rectangle? virtualBounds = null)
    {
        _matrix = matrix;
        VirtualBounds = virtualBounds;
    }

    /// <summary>Replaces the transform matrix (call when the projection changes).</summary>
    public void SetMatrix(Matrix matrix) => _matrix = matrix;

    /// <inheritdoc/>
    public Vector2 ScreenToVirtual(Vector2 screen) => Vector2.Transform(screen, _matrix);

    /// <inheritdoc/>
    public Rectangle? VirtualBounds { get; }
}
