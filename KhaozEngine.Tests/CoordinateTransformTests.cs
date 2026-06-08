using KhaozEngine.Input;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class CoordinateTransformTests
{
    [Fact]
    public void IdentityReturnsInputAndNoBounds()
    {
        var t = IdentityTransform.Instance;
        Assert.Equal(new Vector2(7, 9), t.ScreenToVirtual(new Vector2(7, 9)));
        Assert.Null(t.VirtualBounds);
    }

    [Fact]
    public void MatrixTransformAppliesMatrixAndExposesBounds()
    {
        var t = new MatrixTransform(Matrix.CreateScale(0.5f), new Rectangle(0, 0, 100, 100));
        Assert.Equal(new Vector2(10, 20), t.ScreenToVirtual(new Vector2(20, 40)));
        Assert.Equal(new Rectangle(0, 0, 100, 100), t.VirtualBounds);
    }
}
