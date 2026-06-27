using System;
using System.IO;
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
    }
}
