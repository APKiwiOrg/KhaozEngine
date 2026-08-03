using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>Headless tests for the region runtime: MapRuntime.BuildRegions assembly (document order,
    /// null-shape entries skipped) and MapRegionSet.RegionAt point resolution, including the nearest-center
    /// tiebreak among overlapping regions and the optional filter.</summary>
    public class MapRegionSetTests
    {
        [Fact]
        public void BuildRegions_EmptyDocument_ResolvesNothing()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            doc.Regions.Clear();
            MapRegionSet set = MapRuntime.BuildRegions(doc);
            Assert.Empty(set.Regions);
            Assert.Null(set.RegionAt(0f, 0f));
        }

        [Fact]
        public void RegionAt_DiscRegion_ContainsInsideNotOutside()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            doc.Regions.Clear();
            doc.Regions.Add(new MapRegion { Name = "isle", Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 10f } });
            MapRegionSet set = MapRuntime.BuildRegions(doc);
            Assert.Equal("isle", set.RegionAt(3f, 4f)?.Name);
            Assert.Null(set.RegionAt(20f, 0f));
        }

        [Fact]
        public void RegionAt_OverlappingRegions_NearestCenterWins()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            doc.Regions.Clear();
            doc.Regions.Add(new MapRegion { Name = "big", Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 100f } });
            doc.Regions.Add(new MapRegion { Name = "near", Shape = new DiscShapeDoc { CenterX = 40f, CenterZ = 0f, Radius = 20f } });
            MapRegionSet set = MapRuntime.BuildRegions(doc);
            Assert.Equal("near", set.RegionAt(45f, 0f)?.Name);
            Assert.Equal("big", set.RegionAt(5f, 0f)?.Name);
        }

        [Fact]
        public void BuildRegions_NullShape_SkippedSilently()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            doc.Regions.Clear();
            doc.Regions.Add(new MapRegion { Name = "ghost", Shape = null });
            doc.Regions.Add(new MapRegion { Name = "real", Shape = new DiscShapeDoc { Radius = 5f } });
            MapRegionSet set = MapRuntime.BuildRegions(doc);
            Assert.Equal("real", Assert.Single(set.Regions).Name);
        }

        [Fact]
        public void RegionAt_Filter_SkipsFilteredRegions()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            doc.Regions.Clear();
            doc.Regions.Add(new MapRegion { Name = "hidden", Shape = new DiscShapeDoc { Radius = 50f } });
            doc.Regions.Add(new MapRegion { Name = "shown", Shape = new DiscShapeDoc { Radius = 50f } });
            MapRegionSet set = MapRuntime.BuildRegions(doc);
            Assert.Equal("shown", set.RegionAt(1f, 1f, r => r.Name != "hidden")?.Name);
        }
    }
}
