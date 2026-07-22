using System;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using Xunit;
using SampleDocs = KhaozEngine.Tests.MapDoc.MapDocumentFileTests;

namespace KhaozEngine.Tests.MapEditTool
{
    /// <summary>Headless tests for the <c>freeze_zone</c> MCP verb (<see cref="MutationService.FreezeZone"/>): the
    /// whole-zone freeze over the sample document bakes every scatter and companion layer into placements and removes
    /// all four procedural collections in one world-affecting mutation, and a document with no scatter or companion
    /// layers is a no-op that leaves the session clean. Drives the tool through a real session, the same shape the
    /// <c>bake_region</c> verb test uses.</summary>
    public class MutationServiceFreezeZoneTests
    {
        static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-mapedit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static (MapEditSession session, MutationService mutation) OpenSample(string dir)
        {
            string path = Path.Combine(dir, "zone.map.json");
            MapDocumentFile.Save(SampleDocs.SampleDoc(), path);
            var session = new MapEditSession();
            session.Open(path);
            return (session, new MutationService(session));
        }

        [Fact]
        public void FreezeZone_BakesEveryLayerAndStripsProceduralInputs()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                FreezeZoneResult result = mutation.FreezeZone();

                Assert.True(result.Applied);
                Assert.True(result.PlacementCount > 0, "expected the sample zone to freeze some scatter");
                Assert.Equal(1, result.ScatterLayersRemoved);
                Assert.Equal(1, result.CompanionLayersRemoved);
                Assert.Equal(1, result.ExclusionsRemoved);
                Assert.Equal(1, result.ScatterOverridesRemoved);

                // The document is now placements-only: every procedural collection is empty.
                MapSummary summary = session.Summary();
                Assert.Empty(summary.ScatterLayers);
                Assert.Empty(summary.CompanionLayers);
                Assert.Equal(0, summary.ExclusionCount);
                Assert.Equal(0, summary.ScatterOverrideCount);

                // The frozen props landed as authored placements carrying the baked tag and an explicit Y.
                var baked = session.WithDocument((doc, _) =>
                    doc.Placements.Where(p => p.Tags.Contains("baked")).ToList());
                Assert.Equal(result.PlacementCount, baked.Count);
                Assert.All(baked, p => Assert.NotNull(p.Y));
                Assert.Contains(baked, p => p.Tags.Contains("trees"));        // host layer source tag
                Assert.Contains(baked, p => p.Tags.Contains("understory"));  // companion layer source tag

                // The frozen document still validates and saves.
                ValidateResult v = session.Validate();
                Assert.True(v.StructuralValid, string.Join("; ", v.StructuralErrors));
                Assert.True(session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void FreezeZone_NoScatterOrCompanionLayers_IsCleanNoOp()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "bare.map.json");
                var session = new MapEditSession();
                // Create makes a document with one default biome band but no scatter or companion layers.
                session.Create(path, "bare", "Bare", -10f, -10f, 10f, 10f);
                var mutation = new MutationService(session);
                Assert.False(session.IsDirty);

                FreezeZoneResult result = mutation.FreezeZone();

                Assert.False(result.Applied);
                Assert.Equal(0, result.PlacementCount);
                Assert.Equal(0, result.ScatterLayersRemoved);
                Assert.Equal(0, result.CompanionLayersRemoved);
                Assert.Equal(0, result.ExclusionsRemoved);
                Assert.Equal(0, result.ScatterOverridesRemoved);
                Assert.False(session.IsDirty);   // a true no-op never marks the session dirty
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
