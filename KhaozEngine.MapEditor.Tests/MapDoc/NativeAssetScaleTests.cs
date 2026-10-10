using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public class NativeAssetScaleTests
{
    [Fact]
    public void NativeLoader_KeepsSourceUnitsWithoutHeightNormalization()
    {
        var f = NativeEditorAssetFixtures.ScaledMeshAsset(sourceUnitsToMetres: 0.01f, rawMaxY: 250f);
        Assert.Equal(2.5f, NativeEditorAssetFixtures.MaxVertexY(NativeMapAssetLoader.Load(f.Asset, f.Closure)), 4);
    }
}
