using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using KhaozEngine.Terrain;
using Xunit;
using SampleDocs = KhaozEngine.Tests.MapDoc.MapDocumentFileTests;

namespace KhaozEngine.Tests.MapEditTool
{
    /// <summary>Headless tests for <see cref="QueryService"/>: ground sampling, walkability (slope composed with
    /// a water gate), rect scans over placements/spawns and a scatter layer preview, and the brute-force flat-area
    /// search. All queries run against <see cref="SampleDocs.SampleDoc"/> opened through a fresh session.</summary>
    public class QueryServiceTests
    {
        static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-mapedit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static (MapEditSession session, QueryService query) OpenSample(string dir)
        {
            string path = Path.Combine(dir, "zone.map.json");
            MapDocumentFile.Save(SampleDocs.SampleDoc(), path);
            var session = new MapEditSession();
            session.Open(path);
            return (session, new QueryService(session));
        }

        [Fact]
        public void GroundHeight_MatchesFieldSample()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, QueryService query) = OpenSample(dir);
                TerrainField field = session.Field();

                GroundInfo info = query.GroundHeight(10f, -5f);

                Assert.Equal(10f, info.X);
                Assert.Equal(-5f, info.Z);
                Assert.Equal(field.SampleHeight(10f, -5f), info.Height);
                Assert.Equal(field.WaterLevel, info.WaterLevel);
                Assert.Equal(info.Height < field.WaterLevel, info.BelowWater);

                float expectedSlope = MathF.Acos(Math.Clamp(field.SampleNormal(10f, -5f).Y, 0f, 1f)) * 180f / MathF.PI;
                Assert.Equal(expectedSlope, info.SlopeDegrees, precision: 4);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void IsWalkable_FlatMeadowIsWalkable_CliffIsNot()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, QueryService query) = OpenSample(dir);

                // The flatten feature's center is fully flat: walkable at the default 45-degree gate.
                WalkableInfo flat = query.IsWalkable(-32f, 22f, maxSlopeDegrees: 45f);
                Assert.True(flat.Walkable);
                Assert.False(flat.BelowWater);

                // Scan the doc bounds for a spot steep enough (> 1 degree slope) to fail a near-zero gate.
                TerrainField field = session.Field();
                float steepX = 0f, steepZ = 0f;
                bool found = false;
                for (float x = -100f; x <= 100f && !found; x += 2f)
                {
                    for (float z = -100f; z <= 100f && !found; z += 2f)
                    {
                        Vector3 normal = field.SampleNormal(x, z);
                        float slopeDegrees = MathF.Acos(Math.Clamp(normal.Y, 0f, 1f)) * 180f / MathF.PI;
                        if (slopeDegrees > 1f)
                        {
                            steepX = x;
                            steepZ = z;
                            found = true;
                        }
                    }
                }
                Assert.True(found, "expected to find a spot with slope > 1 degree in the sample doc");

                WalkableInfo steep = query.IsWalkable(steepX, steepZ, maxSlopeDegrees: 0.01f);
                Assert.False(steep.Walkable);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void IsWalkable_BelowWater_NotWalkable()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, QueryService query) = OpenSample(dir);

                GroundInfo ground = query.GroundHeight(-32f, 22f);
                Assert.False(ground.BelowWater);

                session.Mutate((d, r) => { d.Terrain.WaterLevel = ground.Height + 10f; return 0; }, worldChanged: true);

                WalkableInfo result = query.IsWalkable(-32f, 22f, maxSlopeDegrees: 45f);
                Assert.False(result.Walkable);
                Assert.True(result.BelowWater);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void PlacementsInRect_FiltersInclusiveAndResolvesY()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, QueryService query) = OpenSample(dir);

                // Add an explicit-Y placement inside the same rect as the sample doc's null-Y "inn".
                session.Mutate((d, r) =>
                {
                    d.Placements.Add(new MapPlacement { Id = "tower", Kind = "tower_a", X = -25f, Z = 15f, Y = 42f });
                    return 0;
                }, worldChanged: false);

                TerrainField field = session.Field();

                // "inn" is at (-30, 20), "tower" at (-25, 15): both inside this rect.
                PlacementsInRectResult inRect = query.PlacementsInRect(-40f, 10f, -20f, 30f);
                Assert.Equal(2, inRect.Placements.Count);

                PlacementEntry inn = Assert.Single(inRect.Placements, p => p.Id == "inn");
                Assert.False(inn.ExplicitY);
                Assert.Equal(field.SampleHeight(-30f, 20f), inn.Y);
                Assert.Equal("building_inn", inn.Kind);

                PlacementEntry tower = Assert.Single(inRect.Placements, p => p.Id == "tower");
                Assert.True(tower.ExplicitY);
                Assert.Equal(42f, tower.Y);

                // "wolf-1" is at (20, 20), outside this rect.
                Assert.Empty(inRect.Spawns);

                // A rect containing the spawn resolves its ground Y.
                PlacementsInRectResult spawnRect = query.PlacementsInRect(10f, 10f, 30f, 30f);
                SpawnEntry wolf = Assert.Single(spawnRect.Spawns);
                Assert.Equal("wolf-1", wolf.Id);
                Assert.Equal("wolf", wolf.ArchetypeId);
                Assert.Equal(field.SampleHeight(20f, 20f), wolf.GroundY);
                Assert.True(wolf.Enabled);

                // A disjoint rect finds nothing.
                PlacementsInRectResult disjoint = query.PlacementsInRect(60f, 60f, 90f, 90f);
                Assert.Empty(disjoint.Placements);
                Assert.Empty(disjoint.Spawns);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ScatterPreviewInRect_MatchesPropScatterGenerate()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, QueryService query) = OpenSample(dir);

                ScatterPreviewResult preview = query.ScatterPreviewInRect("trees", -100f, -100f, 100f, 100f, maxResults: 10000);

                TerrainField field = session.Field();
                var config = session.WithDocument((d, r) => MapRuntime.BuildScatterConfig(d, "trees"));
                System.Collections.Generic.IReadOnlyList<PropPlacement> direct =
                    PropScatter.Generate(field, config, new RectArea(-100f, -100f, 100f, 100f));

                static string KeyEntry(ScatterEntry e) => $"{e.Kind}|{e.X:F4}|{e.Y:F4}|{e.Z:F4}|{e.Yaw:F4}|{e.Scale:F4}";
                static string KeyPlacement(PropPlacement p) => $"{p.Id}|{p.X:F4}|{p.Y:F4}|{p.Z:F4}|{p.Yaw:F4}|{p.Scale:F4}";

                Assert.True(direct.Count > 0);
                Assert.Equal(direct.Count, preview.Total);
                Assert.False(preview.Truncated);
                Assert.Equal(direct.Select(KeyPlacement).OrderBy(k => k), preview.Entries.Select(KeyEntry).OrderBy(k => k));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ScatterPreviewInRect_UnknownLayer_Throws()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, QueryService query) = OpenSample(dir);

                MapDocumentException ex = Assert.Throws<MapDocumentException>(() =>
                    query.ScatterPreviewInRect("does-not-exist", -10f, -10f, 10f, 10f));
                Assert.Contains("does-not-exist", ex.Message);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ScatterPreviewInRect_CapsAndFlagsTruncation()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, QueryService query) = OpenSample(dir);

                ScatterPreviewResult full = query.ScatterPreviewInRect("trees", -100f, -100f, 100f, 100f, maxResults: 10000);
                Assert.True(full.Total > 1, "expected more than one scattered tree in the sample doc for a meaningful cap test");

                ScatterPreviewResult capped = query.ScatterPreviewInRect("trees", -100f, -100f, 100f, 100f, maxResults: 1);
                Assert.Equal(full.Total, capped.Total);
                Assert.True(capped.Truncated);
                Assert.Single(capped.Entries);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void FindFlatArea_FindsSpotsOnFlattenedDisc()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, QueryService query) = OpenSample(dir);
                TerrainField field = session.Field();

                FlatAreaResult result = query.FindFlatArea(radius: 5f);

                Assert.NotEmpty(result.Spots);
                Assert.Equal(5f, result.Radius);

                // The sample doc's flatten feature is centered at (-32, 22) with radius 34: at least one
                // returned spot must land inside that disc.
                Assert.Contains(result.Spots, s =>
                    MathF.Sqrt(MathF.Pow(s.X - -32f, 2f) + MathF.Pow(s.Z - 22f, 2f)) <= 34f);

                foreach (FlatSpot spot in result.Spots)
                {
                    Assert.True(spot.MaxSlopeDegrees <= 30f, $"spot ({spot.X},{spot.Z}) exceeded the default slope gate");
                    Assert.True(spot.HeightSpread <= 1.0f, $"spot ({spot.X},{spot.Z}) exceeded the default height-spread gate");
                    Assert.True(spot.GroundHeight >= field.WaterLevel, $"spot ({spot.X},{spot.Z}) is below water");
                    Assert.Equal(field.SampleHeight(spot.X, spot.Z), spot.GroundHeight);
                }
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void FindFlatArea_IsDeterministic()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, QueryService query) = OpenSample(dir);

                FlatAreaResult first = query.FindFlatArea(radius: 5f);
                FlatAreaResult second = query.FindFlatArea(radius: 5f);

                Assert.Equal(first.Spots, second.Spots);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
