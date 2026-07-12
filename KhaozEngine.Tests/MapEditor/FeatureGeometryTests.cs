using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for <see cref="FeatureGeometry.CreateDefault"/>: the click-place tool's
    /// default-parameterized feature per type. Covers the ridge no-pass regression (a placed ridge used to carve a
    /// valley exactly at the click point because the default pass sat there).</summary>
    public class FeatureGeometryTests
    {
        [Fact]
        public void EditorRidge_CreateDefault_HasNoNotchAtClickPoint()
        {
            MapFeature? feature = FeatureGeometry.CreateDefault("ridge", 10f, 20f, groundHeight: 0f);
            var ridgeDoc = Assert.IsType<RidgeFeatureDoc>(feature);

            var doc = new MapDocument { Id = "ridge-default-test", Bounds = new MapBounds { MinX = -50f, MinZ = -50f, MaxX = 50f, MaxZ = 50f } };
            doc.Terrain.Features.Add(ridgeDoc);

            var registry = MapDocRegistry.CreateDefault();
            var withRidge = MapRuntime.BuildField(doc, registry);

            doc.Terrain.Features.Clear();
            var baseline = MapRuntime.BuildField(doc, registry);

            // The ridge's contribution at the click point itself: with the old always-on pass this was ~0
            // (a carved dip right where the player clicked). The new opt-in default is a solid wall, so the
            // contribution at the click point is the full crest height.
            float contribution = withRidge.SampleHeight(10f, 20f) - baseline.SampleHeight(10f, 20f);
            Assert.Equal(ridgeDoc.Height, contribution, 2);
        }
    }
}
