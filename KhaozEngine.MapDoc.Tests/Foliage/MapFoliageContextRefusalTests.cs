using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageContextRefusalTests
{
    [Theory]
    [InlineData("MaxSourceUtf8Bytes")]
    [InlineData("MaxChunkDescriptors")]
    [InlineData("MaxPlacements")]
    [InlineData("MaxSurfacePatches")]
    [InlineData("MaxPaintTargets")]
    public void ContextLimits_KeepTheBclContractAndTheFailedNestedField(string field)
    {
        var fixture = FoliageFixtures.Create();
        var control = new MapFoliageContextLimits();
        var broken = field switch
        {
            "MaxSourceUtf8Bytes" => control with { MaxSourceUtf8Bytes = -1 },
            "MaxChunkDescriptors" => control with { MaxChunkDescriptors = -1 },
            "MaxPlacements" => control with { MaxPlacements = -1 },
            "MaxSurfacePatches" => control with { MaxSurfacePatches = -1 },
            _ => control with { MaxPaintTargets = -1 },
        };
        var error = Assert.Throws<MapFoliageContextArgumentException>(() => MapFoliageGenerationContext.Capture(fixture.Document, fixture.Assets, broken));
        Assert.IsAssignableFrom<System.ArgumentOutOfRangeException>(error);
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, error.Reason);
        Assert.Equal("limits", error.ParamName);
        Assert.Null(error.ActualValue);
        Assert.Equal(field, error.FieldPath);
        Assert.Equal(-1L, error.ActualInteger);
        Assert.Equal(0L, error.MinimumInteger);
        var boundary = field switch
        {
            "MaxSourceUtf8Bytes" => control with { MaxSourceUtf8Bytes = 0 },
            "MaxChunkDescriptors" => control with { MaxChunkDescriptors = 0 },
            "MaxPlacements" => control with { MaxPlacements = 0 },
            "MaxSurfacePatches" => control with { MaxSurfacePatches = 0 },
            _ => control with { MaxPaintTargets = 0 },
        };
        // Zero admits the option itself even when the document's demand then returns CapacityExceeded.
        Assert.NotNull(MapFoliageGenerationContext.Capture(fixture.Document, fixture.Assets, boundary));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EffectiveContextAdmission_IdentifiesTheMissingCapturedSource(bool finalization)
    {
        var fixture = FoliageFixtures.Create();
        var source = MapDocumentSurfaceSource.Capture(fixture.Document);
        var error = Assert.Throws<MapDocumentException>(() => Require(source));
        Assert.Equal(MapDocumentRefusal.FoliageEffectiveContextRequired, error.Reason);
        Assert.Equal(typeof(MapDocumentSurfaceSource), error.SourceType);
        Assert.Equal(finalization ? "foliage finalization" : "foliage context", error.Operation);
        Require(MapPrefabResolver.Expand(fixture.Document, fixture.Assets).SurfaceSource);

        void Require(IMapSurfaceSource value)
        {
            if (finalization) MapFoliageChangedInputs.RequireEffectiveContextForTests(value);
            else MapFoliageGenerationContext.RequireEffectiveContextForTests(value);
        }
    }
}
