using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Content;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc
{
    /// <summary>Headless tests for map document load/save: round-trip fidelity, loud validation failures,
    /// version migrations, too-new rejection, and JSONC tolerance.</summary>
    public class MapDocumentFileTests
    {
        internal static MapDocument SampleDoc()
        {
            var doc = new MapDocument
            {
                Id = "test-zone",
                DisplayName = "Test Zone",
                Bounds = new MapBounds { MinX = -100f, MinZ = -100f, MaxX = 100f, MaxZ = 100f },
            };
            doc.Terrain.Seed = 7345;
            doc.Terrain.WaterLevel = -0.5f;
            doc.Terrain.Biomes.Add(new MapBiomeBand { Biome = KhaozEngine.Terrain.BiomeId.Marsh, BaseHeight = 1.5f, HillAmplitude = 1.2f });
            doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 34f, CenterZ = -14f, Radius = 22f, Depth = 6f });
            doc.Terrain.Features.Add(new FlattenFeatureDoc { CenterX = -32f, CenterZ = 22f, Radius = 34f, TargetHeight = 2f, Blend = 0.25f });
            doc.ScatterLayers.Add(new MapScatterLayer
            {
                Name = "trees",
                Seed = 0x52424E,
                CellSize = 5f,
                Rules = { new MapBiomeScatterRule { Biome = KhaozEngine.Terrain.BiomeId.Marsh, Density = 0.35f, Kinds = { new MapPropKind { Id = "pine_a", Weight = 1f } } } },
            });
            doc.CompanionLayers.Add(new MapCompanionLayer
            {
                Name = "understory", HostLayer = "trees", HostKinds = { "pine_a" },
                Kinds = { new MapPropKind { Id = "fern", Weight = 1f } },
            });
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = -32f, CenterZ = 22f, Radius = 30f } });
            doc.ScatterOverrides.Add(new MapScatterOverrideDoc
            {
                Shape = new RectShapeDoc { MinX = 0f, MinZ = 0f, MaxX = 50f, MaxZ = 50f },
                DensityMultiplier = 0.5f,
                Layers = new() { "trees" },
            });
            doc.Placements.Add(new MapPlacement { Id = "inn", Kind = "building_inn", X = -30f, Z = 20f, Yaw = 1.2f });
            doc.Spawns.Add(new MapSpawn { Id = "wolf-1", ArchetypeId = "wolf", X = 20f, Z = 20f });
            doc.Regions.Add(new MapRegion { Name = "town", Shape = new DiscShapeDoc { CenterX = -32f, CenterZ = 22f, Radius = 34f }, Tags = { "safe" } });
            return doc;
        }

        /// <summary>Rewrites the formatVersion of a just-saved document, keyed off the version this build
        /// writes so the version bump does not have to be chased through every test that fakes an old or a
        /// malformed document.</summary>
        internal static string WithFormatVersion(string json, object version) => json.Replace(
            $"\"formatVersion\": {MapDocumentFile.CurrentFormatVersion}", $"\"formatVersion\": {version}");

        [Fact]
        public void SaveText_LoadText_RoundTripsEverySection()
        {
            var doc = SampleDoc();
            string json = MapDocumentFile.SaveText(doc);
            var back = MapDocumentFile.LoadText(json);

            Assert.Equal(doc.Id, back.Id);
            Assert.Equal(doc.Bounds.MaxX, back.Bounds.MaxX);
            Assert.Equal(doc.Terrain.Seed, back.Terrain.Seed);
            Assert.Equal(2, back.Terrain.Features.Count);
            Assert.IsType<LakeFeatureDoc>(back.Terrain.Features[0]);
            Assert.Equal(0.35f, back.ScatterLayers[0].Rules[0].Density);
            Assert.Equal("trees", back.CompanionLayers[0].HostLayer);
            Assert.IsType<DiscShapeDoc>(back.Exclusions[0].Shape);
            Assert.Equal(0.5f, back.ScatterOverrides[0].DensityMultiplier);
            Assert.Equal("building_inn", back.Placements[0].Kind);
            Assert.Null(back.Placements[0].Y);
            Assert.True(back.Spawns[0].Enabled);
            Assert.Contains("safe", back.Regions[0].Tags);
        }

        [Fact]
        public void RidgeDoc_Defaults_RoundTripAsSolidWall()
        {
            // A bare ridge (no PassWidth set) round-trips at the 0f DTO default: no pass, a solid wall.
            var doc = new MapDocument { Id = "ridge-test", Bounds = new MapBounds { MinX = -50f, MinZ = -50f, MaxX = 50f, MaxZ = 50f } };
            doc.Terrain.Features.Add(new RidgeFeatureDoc { PointX = 0f, PointZ = 0f, Height = 12f, Width = 4f });

            string json = MapDocumentFile.SaveText(doc);
            var back = MapDocumentFile.LoadText(json);

            var ridgeDoc = Assert.IsType<RidgeFeatureDoc>(back.Terrain.Features[0]);
            Assert.Equal(0f, ridgeDoc.PassWidth);

            var ridge = ridgeDoc.Build();
            Assert.Equal(12f, ridge.Apply(0f, 0f, 0f), 3);   // gate-free: full crest right at the point
        }

        [Fact]
        public void PolygonShape_SaveLoadRoundTripsExactly()
        {
            var doc = SampleDoc();
            var points = new List<float[]>
            {
                new float[] { -10f, -10f },
                new float[] { 10f, -10f },
                new float[] { 15f, 0f },
                new float[] { 10f, 10f },
                new float[] { -10f, 10f },
            };
            doc.Exclusions.Add(new MapExclusion { Shape = new PolygonShapeDoc { Points = points } });

            string json = MapDocumentFile.SaveText(doc);
            var back = MapDocumentFile.LoadText(json);

            var polygon = Assert.IsType<PolygonShapeDoc>(back.Exclusions[^1].Shape);
            Assert.Equal(points.Count, polygon.Points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                Assert.Equal(points[i][0], polygon.Points[i][0]);
                Assert.Equal(points[i][1], polygon.Points[i][1]);
            }

            ValidationReport report = JsonSchemaValidator.Validate(json, MapDocumentSchema.GetJson());
            Assert.True(report.IsValid, string.Join("\n", report.Errors));
        }

        [Fact]
        public void Load_RoundTripsThroughDisk()
        {
            string path = Path.Combine(Path.GetTempPath(), $"mapdoc-test-{Guid.NewGuid():N}.map.json");
            try
            {
                MapDocumentFile.Save(SampleDoc(), path);
                var back = MapDocumentFile.Load(path);
                Assert.Equal("test-zone", back.Id);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadText_ToleratesJsoncCommentsAndTrailingCommas()
        {
            string json = MapDocumentFile.SaveText(SampleDoc());
            string jsonc = "// zone doc\n" + json.Replace("\"displayName\": \"Test Zone\",", "\"displayName\": \"Test Zone\", // name");
            var back = MapDocumentFile.LoadText(jsonc);
            Assert.Equal("Test Zone", back.DisplayName);
        }

        [Fact]
        public void LoadText_InvalidJson_ThrowsWithSource()
        {
            var ex = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText("{ not json", sourcePath: "bad.map.json"));
            Assert.Contains("bad.map.json", ex.Message);
        }

        [Fact]
        public void LoadText_NonObjectRoot_ThrowsWithSource()
        {
            var ex = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText("[1, 2, 3]", sourcePath: "arr.map.json"));
            Assert.Contains("arr.map.json", ex.Message);
            Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText("42"));
        }

        [Fact]
        public void LoadText_MissingFormatVersion_Throws()
        {
            Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText("{\"id\":\"x\"}"));
        }

        [Fact]
        public void LoadText_TooNewVersion_Throws()
        {
            string json = WithFormatVersion(MapDocumentFile.SaveText(SampleDoc()), 99);
            var ex = Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(json));
            Assert.Contains("99", ex.Message);
        }

        [Fact]
        public void LoadText_OldVersionWithoutMigration_Throws()
        {
            // formatVersion 0 has no registered migration path (the built-in steps start at 1 -> 2).
            string json = WithFormatVersion(MapDocumentFile.SaveText(SampleDoc()), 0);
            Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(json));
        }

        [Fact]
        public void LoadText_RegisteredMigration_Runs()
        {
            // Synthetic v0: displayName lived under "name". The custom 0 -> 1 step renames it, then the
            // built-in steps run, so a v0 document climbs the whole chain to the current version.
            string json = WithFormatVersion(MapDocumentFile.SaveText(SampleDoc()), 0)
                .Replace("\"displayName\": \"Test Zone\"", "\"name\": \"Test Zone\"");
            var options = new MapDocumentLoadOptions();
            options.RegisterMigration(0, root =>
            {
                root["displayName"] = root["name"]?.GetValue<string>();
                root.Remove("name");
                return root;
            });
            var back = MapDocumentFile.LoadText(json, options);
            Assert.Equal("Test Zone", back.DisplayName);
            Assert.Equal(MapDocumentFile.CurrentFormatVersion, back.FormatVersion);
        }

        [Fact]
        public void Validate_CatchesTheLoudFailures()
        {
            var registry = MapDocRegistry.CreateDefault();

            var noId = SampleDoc(); noId.Id = "";
            Assert.Contains(MapDocumentValidator.Validate(noId, registry), e => e.Contains("id"));

            var badBounds = SampleDoc(); badBounds.Bounds.MaxX = badBounds.Bounds.MinX;
            Assert.Contains(MapDocumentValidator.Validate(badBounds, registry), e => e.Contains("bounds"));

            var dupPlacement = SampleDoc();
            dupPlacement.Placements.Add(new MapPlacement { Id = "inn", Kind = "building_inn", X = 0f, Z = 0f });
            Assert.Contains(MapDocumentValidator.Validate(dupPlacement, registry), e => e.Contains("inn"));

            var badHost = SampleDoc(); badHost.CompanionLayers[0].HostLayer = "nope";
            Assert.Contains(MapDocumentValidator.Validate(badHost, registry), e => e.Contains("nope"));

            var badLayerRef = SampleDoc(); badLayerRef.Exclusions[0].Layers = new() { "nope" };
            Assert.Contains(MapDocumentValidator.Validate(badLayerRef, registry), e => e.Contains("nope"));

            var badCell = SampleDoc(); badCell.ScatterLayers[0].CellSize = 0f;
            Assert.Contains(MapDocumentValidator.Validate(badCell, registry), e => e.Contains("cellSize"));

            var noShape = SampleDoc(); noShape.Exclusions[0].Shape = null;
            Assert.Contains(MapDocumentValidator.Validate(noShape, registry), e => e.Contains("shape"));

            Assert.Empty(MapDocumentValidator.Validate(SampleDoc(), registry));
        }

        [Fact]
        public void SaveText_InvalidDocument_Throws()
        {
            var doc = SampleDoc();
            doc.Id = "";
            Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveText(doc));
        }

        [Fact]
        public void PlayerSpawn_RoundTripsThroughSaveLoad_PassesSchema()
        {
            var doc = SampleDoc();
            doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start", X = 12f, Z = -8f, Yaw = 1.57f });
            doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "rally", X = -4f, Z = 30f, Enabled = false, Tags = { "co-op", "north" } });

            string json = MapDocumentFile.SaveText(doc);
            var back = MapDocumentFile.LoadText(json);

            Assert.Equal(2, back.PlayerSpawns.Count);
            Assert.Equal("start", back.PlayerSpawns[0].Id);
            Assert.Equal(12f, back.PlayerSpawns[0].X);
            Assert.Equal(-8f, back.PlayerSpawns[0].Z);
            Assert.Equal(1.57f, back.PlayerSpawns[0].Yaw);
            Assert.True(back.PlayerSpawns[0].Enabled);            // default true survives round trip
            Assert.False(back.PlayerSpawns[1].Enabled);
            Assert.Contains("co-op", back.PlayerSpawns[1].Tags);
            Assert.Contains("north", back.PlayerSpawns[1].Tags);

            ValidationReport report = JsonSchemaValidator.Validate(json, MapDocumentSchema.GetJson());
            Assert.True(report.IsValid, string.Join("\n", report.Errors));
        }

        [Fact]
        public void PlayerSpawn_DuplicateIds_FailValidation()
        {
            var registry = MapDocRegistry.CreateDefault();

            var dup = SampleDoc();
            dup.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start", X = 0f, Z = 0f });
            dup.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start", X = 1f, Z = 1f });
            Assert.Contains(MapDocumentValidator.Validate(dup, registry), e => e.Contains("start"));

            var empty = SampleDoc();
            empty.PlayerSpawns.Add(new MapPlayerSpawn { Id = "", X = 0f, Z = 0f });
            Assert.Contains(MapDocumentValidator.Validate(empty, registry), e => e.Contains("player spawn"));

            // A single well-formed player spawn is valid.
            var ok = SampleDoc();
            ok.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start", X = 0f, Z = 0f });
            Assert.Empty(MapDocumentValidator.Validate(ok, registry));
        }
    }
}
