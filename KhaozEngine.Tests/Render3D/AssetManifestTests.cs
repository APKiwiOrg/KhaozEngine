using System;
using System.IO;
using KhaozEngine.Collision;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Headless tests for the prop-kit asset manifest parser (no GPU, no real asset): a JSON
    /// { props: [ { id, file, heightMeters, source, license } ] } parses into entries, relative file paths
    /// resolve against the manifest directory, and malformed JSON fails cleanly.</summary>
    public class AssetManifestTests
    {
        const string Json = """
        {
          "props": [
            { "id": "pine_a", "file": "pine_a.glb", "heightMeters": 14, "source": "Quaternius", "license": "CC0" },
            { "id": "rock_a", "file": "sub/rock_a.glb", "heightMeters": 2.0, "source": "Quaternius", "license": "CC0" }
          ]
        }
        """;

        [Fact]
        public void Parse_ReadsEntries()
        {
            var m = AssetManifest.Parse(Json);
            Assert.Equal(2, m.Props.Count);

            AssetEntry pine = m.Props[0];
            Assert.Equal("pine_a", pine.Id);
            Assert.Equal("pine_a.glb", pine.File);
            Assert.Equal(14f, pine.HeightMeters, 4);
            Assert.Equal("Quaternius", pine.Source);
            Assert.Equal("CC0", pine.License);

            Assert.Equal(2f, m.Props[1].HeightMeters, 4);
        }

        [Fact]
        public void Parse_ResolvesRelativeFileAgainstBaseDir()
        {
            string baseDir = Path.Combine("X", "kit");
            var m = AssetManifest.Parse(Json, baseDir);
            Assert.Equal(Path.Combine(baseDir, "pine_a.glb"), m.Props[0].File);
            Assert.Equal(Path.Combine(baseDir, "sub", "rock_a.glb"), m.Props[1].File);
        }

        [Fact]
        public void Load_ResolvesRelativeFileAgainstManifestDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke_manifest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "props.manifest.json");
            File.WriteAllText(path, Json);
            try
            {
                var m = AssetManifest.Load(path);
                Assert.Equal(Path.Combine(dir, "pine_a.glb"), m.Props[0].File);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Parse_GarbledJson_Throws()
        {
            Assert.ThrowsAny<Exception>(() => AssetManifest.Parse("{ not json"));
        }

        [Fact]
        public void Find_ReturnsEntryOrNull()
        {
            var m = AssetManifest.Parse(Json);
            Assert.Equal("pine_a", m.Find("pine_a")!.Value.Id);
            Assert.Null(m.Find("missing"));
        }

        [Fact]
        public void Parse_CylinderCollider()
        {
            const string json = """
            { "props": [ { "id": "pine", "file": "pine.glb", "heightMeters": 6,
                           "collider": { "type": "cylinder", "radius": 0.45 } } ] }
            """;
            AssetManifest m = AssetManifest.Parse(json);
            ColliderShape? col = m.Props[0].Collider;
            Assert.True(col.HasValue);
            Assert.Equal(ColliderKind.Cylinder, col!.Value.Kind);
            Assert.Equal(0.45f, col.Value.Radius, 4);
        }

        [Fact]
        public void Parse_BoxCollider()
        {
            const string json = """
            { "props": [ { "id": "inn", "file": "inn.glb", "heightMeters": 5,
                           "collider": { "type": "box", "halfW": 3, "halfD": 2 } } ] }
            """;
            AssetManifest m = AssetManifest.Parse(json);
            ColliderShape? col = m.Props[0].Collider;
            Assert.True(col.HasValue);
            Assert.Equal(ColliderKind.Box, col!.Value.Kind);
            Assert.Equal(3f, col.Value.HalfW, 4);
            Assert.Equal(2f, col.Value.HalfD, 4);
        }

        [Fact]
        public void Parse_NoCollider_IsNull()
        {
            const string json = """
            { "props": [ { "id": "rock", "file": "rock.glb", "heightMeters": 1 } ] }
            """;
            AssetManifest m = AssetManifest.Parse(json);
            Assert.False(m.Props[0].Collider.HasValue);
        }

        [Fact]
        public void Parse_UnknownColliderType_Throws()
        {
            const string json = """
            { "props": [ { "id": "x", "file": "x.glb", "heightMeters": 1,
                           "collider": { "type": "sphere" } } ] }
            """;
            Assert.ThrowsAny<Exception>(() => AssetManifest.Parse(json));
        }

        [Fact]
        public void Parse_HeightmapAndSurfaceFlag()
        {
            const string json = """
            { "props": [ { "id": "rock", "file": "rock.glb", "heightMeters": 1.8,
                           "surface": true, "heightmap": "rock.surf" } ] }
            """;
            AssetManifest m = AssetManifest.Parse(json);
            Assert.True(m.Props[0].Surface);
            Assert.Equal("rock.surf", m.Props[0].Heightmap);
        }

        [Fact]
        public void Parse_NoHeightmap_DefaultsNullAndFalse()
        {
            const string json = """{ "props": [ { "id": "x", "file": "x.glb", "heightMeters": 1 } ] }""";
            AssetManifest m = AssetManifest.Parse(json);
            Assert.Null(m.Props[0].Heightmap);
            Assert.False(m.Props[0].Surface);
        }

        [Fact]
        public void Parse_RelativeHeightmap_ResolvesAgainstBaseDir()
        {
            const string json = """
            { "props": [ { "id": "rock", "file": "rock.glb", "heightMeters": 1.8, "heightmap": "rock.surf" } ] }
            """;
            string baseDir = Path.Combine("X", "kit");
            AssetManifest m = AssetManifest.Parse(json, baseDir);
            Assert.Equal(Path.Combine(baseDir, "rock.surf"), m.Props[0].Heightmap);
        }

        [Fact]
        public void Parse_ReadsAndResolvesCollisionProxy()
        {
            string json = """
            { "props": [ { "id": "blacksmith", "file": "blacksmith.glb", "heightMeters": 5.0,
                           "collisionProxy": "blacksmith_collision.glb" } ] }
            """;
            var manifest = AssetManifest.Parse(json, "/kit");
            var e = manifest.Find("blacksmith")!.Value;
            Assert.Equal(System.IO.Path.Combine("/kit", "blacksmith_collision.glb"), e.CollisionProxy);
        }

        [Fact]
        public void Parse_NoCollisionProxy_IsNull()
        {
            string json = """{ "props": [ { "id": "rock", "file": "rock.glb", "heightMeters": 1.0 } ] }""";
            var manifest = AssetManifest.Parse(json, "/kit");
            Assert.Null(manifest.Find("rock")!.Value.CollisionProxy);
        }

        [Fact]
        public void Parse_TexturedFlag_ReadWhenPresent()
        {
            string json = @"{ ""props"": [ { ""id"": ""p"", ""file"": ""p.glb"", ""heightMeters"": 2.0, ""textured"": true } ] }";
            AssetManifest m = AssetManifest.Parse(json);
            Assert.True(m.Props[0].Textured);
        }

        [Fact]
        public void Parse_TexturedFlag_DefaultsFalseWhenAbsent()
        {
            string json = @"{ ""props"": [ { ""id"": ""p"", ""file"": ""p.glb"", ""heightMeters"": 2.0 } ] }";
            AssetManifest m = AssetManifest.Parse(json);
            Assert.False(m.Props[0].Textured);
        }

        [Fact]
        public void AssetManifest_ParsesOptionalCategory()
        {
            const string json = """
            { "props": [ { "id": "pine_a", "file": "pine_a.glb", "heightMeters": 12.0, "category": "trees" } ] }
            """;
            AssetManifest m = AssetManifest.Parse(json);
            Assert.Equal("trees", m.Props[0].Category);
        }

        [Fact]
        public void AssetManifest_CategoryAbsent_IsNull()
        {
            string json = @"{ ""props"": [ { ""id"": ""p"", ""file"": ""p.glb"", ""heightMeters"": 2.0 } ] }";
            AssetManifest m = AssetManifest.Parse(json);
            Assert.Null(m.Props[0].Category);
        }
    }
}
