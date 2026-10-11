using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public class WaterOpticsSceneTests
{
    [Theory]
    [InlineData("ledge", -2f)]
    [InlineData("river", -2f)]
    [InlineData("shelf", -100f)]
    [InlineData("cave-lake", -40f)]
    [InlineData("cross-region", -2f)]
    public void OpticsScenes_ValidateCompileAndReportTheirDepths(string scene, float deepestBed)
    {
        MapDocument doc = WaterOpticsScenes.Build(scene);
        Assert.Empty(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()));
        MapCompiledWaterBody[] bodies = MapWaterCompiler.Compile(CaveFixtures.View(doc), doc.Water!).ToArray();
        Assert.All(bodies, b => Assert.Equal(MapWaterCompileStatus.Complete, b.Status));
        Assert.Equal(deepestBed, bodies.Min(b => b.DeepestBedY));
        Assert.Contains(bodies.SelectMany(b => b.Regions), r => r.Kind == MapWaterRegionKind.FreeSurface && r.Triangles.Count > 0);
    }
}
