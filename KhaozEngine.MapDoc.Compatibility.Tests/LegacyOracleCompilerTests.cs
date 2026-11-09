using System.Linq;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.MapDocCompatibility;

public sealed class LegacyOracleCompilerTests
{
    [Fact]
    public void LegacyOracle_SyntheticWorldMatchesNativeCompiler()
    {
        LegacyOracleWorld w = LegacyOracleWorld.Create();
        var regionPlanes = LegacyOracleConverter.RegionPlanes(w.Document).ToArray();
        Assert.NotEmpty(regionPlanes);
        foreach (var (region, plane) in regionPlanes)
        {
            var (surface, patch) = LegacyOracleConverter.ToNative(w.Document, region, plane);
            MapCompiledPatch native = MapSurfaceCompiler.Compile(surface, patch);
            TileGroundMesh legacy = TileGroundTriangles.Build(w.Document, region, plane);
            Assert.Equal(legacy.Indices.Length / 3, native.Faces.Count);
            LegacyOracleConverter.AssertSameTriangles(legacy, native, vertexTolerance: 0.00001f, normalTolerance: 0.000001f);
            Assert.Equal(LegacyOracleConverter.FallbackCells(w.Document, region, plane), native.LegacyFallbackCells.Select(c => c.SlotCell));
            Assert.Equal(LegacyOracleConverter.CellBytes(w.Document, region, plane), LegacyOracleConverter.CellBytes(patch));
        }
    }
}
