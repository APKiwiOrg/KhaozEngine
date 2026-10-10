using System.Linq;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.MapDocCompatibility;

public sealed class LegacyTerrainImportTests
{
    [Fact]
    public void LegacyTerrainImport_RemainsExactWithCaveModel()
    {
        LegacyOracleWorld w = LegacyOracleWorld.Create();
        int nativeFaces = 0, legacyTriangles = 0;
        foreach (var (region, plane) in LegacyOracleConverter.RegionPlanes(w.Document))
        {
            var (surface, patch) = LegacyOracleConverter.ToNative(w.Document, region, plane);
            Assert.Equal(LegacyOracleConverter.LegacyCorners(w.Document, region, plane), patch.Heights);
            Assert.Equal(LegacyOracleConverter.CellBytes(w.Document, region, plane), LegacyOracleConverter.CellBytes(patch));
            Assert.Empty(patch.Records);
            nativeFaces += MapSurfaceCompiler.Compile(surface, patch).Faces.Count(f => f.Role == MapFaceRole.SupportFloor);
            legacyTriangles += TileGroundTriangles.Build(w.Document, region, plane).Indices.Length / 3;
        }
        Assert.Equal(legacyTriangles, nativeFaces);
        Assert.Empty(LegacyOracleConverter.SharedCornerMismatches(w.Document));
    }
}
