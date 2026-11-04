using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class FoliageMaterialTests
{
    [Theory]
    [InlineData(false, 0f, 0f, true)]
    [InlineData(true, 0f, 0f, false)]
    [InlineData(false, .5f, 0f, false)]
    [InlineData(false, 0f, 1f, false)]
    public void OnlyDefaultOpaqueUntangentedMeshesQualify(bool maps, float cutoff, float tangent, bool expected)
    {
        ModelVertex[] vertices = [new(Vector3.Zero, Vector3.UnitY, Vector4.One),
            new(Vector3.UnitX, Vector3.UnitY, Vector4.One, Vector2.Zero, new Vector4(tangent, 0, 0, 0))];
        Assert.Equal(expected, FoliageMaterial.IsOpaque(maps, cutoff, vertices));
    }
}
