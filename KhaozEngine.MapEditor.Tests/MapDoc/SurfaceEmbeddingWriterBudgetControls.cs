using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceEmbeddingWriterBudgetControls
{
    [Theory]
    [InlineData(0, 2L)]
    [InlineData(1, 242L)]
    [InlineData(2, 483L)]
    public void WriterCheckUsesTheSameExactArrayWrapperBudget(int count, long exactBytes)
    {
        var set = new MapSurfaceSet();
        for (int i = 0; i < count; i++)
        {
            var patch = new MapSurfacePatch
            {
                Key = new("s", i, 0), Width = 1, Depth = 1,
                Heights = new int[4], Cells = new MapSurfaceCell[1], Presence = new ulong[] { 1 },
            };
            set.Patches.Add(patch.Key, patch);
        }
        MapSurfaceEmbedding.Check(set, 2, exactBytes);
        Assert.Contains("tiled", Assert.Throws<MapDocumentException>(() =>
            MapSurfaceEmbedding.Check(set, 2, exactBytes - 1)).Message);
    }
}
