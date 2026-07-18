using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>The spec's determinism guard: a document-driven world enumerates identically chunked vs whole
    /// (streaming safety) and across two independent load-build passes from the same JSON (client/server
    /// parity by construction).</summary>
    public class MapDocDeterminismTests
    {
        static string Key(PropPlacement p) => $"{p.Id}|{p.X:F4}|{p.Y:F4}|{p.Z:F4}|{p.Scale:F4}|{p.Yaw:F4}|{p.Variant}";

        [Fact]
        public void ChunkedEnumeration_EqualsWholeZone()
        {
            var doc = MapDocumentFileTests.SampleDoc();
            var registry = MapDocRegistry.CreateDefault();
            var field = MapRuntime.BuildField(doc, registry);
            var cfg = MapRuntime.BuildScatterConfig(doc, "trees");

            var whole = PropScatter.Generate(field, cfg,
                new RectArea(doc.Bounds.MinX, doc.Bounds.MinZ, doc.Bounds.MaxX, doc.Bounds.MaxZ));

            const float chunk = 30f;   // deliberately does not divide the 200 m zone evenly
            var tiled = new List<PropPlacement>();
            for (float x = doc.Bounds.MinX; x < doc.Bounds.MaxX; x += chunk)
                for (float z = doc.Bounds.MinZ; z < doc.Bounds.MaxZ; z += chunk)
                    tiled.AddRange(PropScatter.Generate(field, cfg,
                        new RectArea(x, z, System.MathF.Min(x + chunk, doc.Bounds.MaxX), System.MathF.Min(z + chunk, doc.Bounds.MaxZ))));

            Assert.True(whole.Count > 0);
            Assert.Equal(
                whole.Select(Key).OrderBy(k => k),
                tiled.Select(Key).OrderBy(k => k));
        }

        [Fact]
        public void TwoIndependentLoads_ProduceIdenticalWorlds()
        {
            string json = MapDocumentFile.SaveText(MapDocumentFileTests.SampleDoc());

            // Two fully independent passes, as a client and a server would each do at boot.
            var a = BuildAll(json);
            var b = BuildAll(json);

            Assert.Equal(a.scatter.Select(Key), b.scatter.Select(Key));
            Assert.Equal(a.companions.Select(Key), b.companions.Select(Key));
            Assert.Equal(a.placements.Select(Key), b.placements.Select(Key));
        }

        static (IReadOnlyList<PropPlacement> scatter, IReadOnlyList<PropPlacement> companions, IReadOnlyList<PropPlacement> placements)
            BuildAll(string json)
        {
            var doc = MapDocumentFile.LoadText(json);
            var registry = MapDocRegistry.CreateDefault();
            var field = MapRuntime.BuildField(doc, registry);
            var area = new RectArea(doc.Bounds.MinX, doc.Bounds.MinZ, doc.Bounds.MaxX, doc.Bounds.MaxZ);
            var scatter = PropScatter.Generate(field, MapRuntime.BuildScatterConfig(doc, "trees"), area);
            var companions = PropScatter.GenerateCompanions(field, scatter, MapRuntime.BuildCompanionConfig(doc, "understory"));
            var placements = MapRuntime.BuildPlacements(doc, field);
            return (scatter, companions, placements);
        }
    }
}
