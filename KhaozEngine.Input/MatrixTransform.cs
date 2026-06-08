using Microsoft.Xna.Framework;

namespace KhaozEngine.Input;

// Wraps an arbitrary screen->virtual matrix (e.g. SpaceGame's inputTransformation).
public sealed class MatrixTransform : ICoordinateTransform
{
    private Matrix _matrix;
    public MatrixTransform(Matrix matrix, Rectangle? virtualBounds = null)
    {
        _matrix = matrix;
        VirtualBounds = virtualBounds;
    }
    public void SetMatrix(Matrix matrix) => _matrix = matrix;
    public Vector2 ScreenToVirtual(Vector2 screen) => Vector2.Transform(screen, _matrix);
    public Rectangle? VirtualBounds { get; }
}
