using System;
using System.IO;
using KeDungeon;
using KhaozEngine.MapDoc;
using Xunit;

namespace KeDungeon.Tests;

public class VerbTests
{
    static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-dungeon-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Generate_Then_Verify_Exits0()
    {
        string dir = NewTempDir();
        try
        {
            string layoutPath = Path.Combine(dir, "layout.json");

            int generateExit = Program.Main(new[] { "generate", "--seed", "42", "--out", layoutPath });
            Assert.Equal(0, generateExit);
            Assert.True(File.Exists(layoutPath));

            int verifyExit = Program.Main(new[] { "verify", "--layout", layoutPath });
            Assert.Equal(0, verifyExit);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Generate_Then_Preview_WritesFloor0Png()
    {
        string dir = NewTempDir();
        try
        {
            string layoutPath = Path.Combine(dir, "layout.json");
            int generateExit = Program.Main(new[] { "generate", "--seed", "7", "--out", layoutPath });
            Assert.Equal(0, generateExit);

            string outDir = Path.Combine(dir, "preview");
            int previewExit = Program.Main(new[] { "preview", "--layout", layoutPath, "--out-dir", outDir });
            Assert.Equal(0, previewExit);

            string floor0 = Path.Combine(outDir, "floor-0.png");
            Assert.True(File.Exists(floor0));

            byte[] bytes = File.ReadAllBytes(floor0);
            Assert.True(bytes.Length > 8);
            Assert.Equal(0x89, bytes[0]);
            Assert.Equal(0x50, bytes[1]);
            Assert.Equal(0x4E, bytes[2]);
            Assert.Equal(0x47, bytes[3]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Bake_ProducesMapJson_ThatMapDocumentFileAccepts()
    {
        string dir = NewTempDir();
        try
        {
            string layoutPath = Path.Combine(dir, "layout.json");
            int generateExit = Program.Main(new[] { "generate", "--seed", "3", "--out", layoutPath });
            Assert.Equal(0, generateExit);

            string mapPath = Path.Combine(dir, "map.json");
            int bakeExit = Program.Main(new[]
            {
                "bake",
                "--layout", layoutPath,
                "--map", mapPath,
                "--origin-x", "0",
                "--origin-z", "0",
                "--base-y", "0",
                "--yaw", "0",
            });
            Assert.Equal(0, bakeExit);
            Assert.True(File.Exists(mapPath));

            MapDocument doc = MapDocumentFile.Load(mapPath);
            Assert.NotEmpty(doc.Placements);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Bake_TwiceIntoSameMap_Accumulates()
    {
        string dir = NewTempDir();
        try
        {
            string firstLayout = Path.Combine(dir, "layout-a.json");
            string secondLayout = Path.Combine(dir, "layout-b.json");
            Assert.Equal(0, Program.Main(new[] { "generate", "--seed", "3", "--out", firstLayout }));
            Assert.Equal(0, Program.Main(new[] { "generate", "--seed", "4", "--out", secondLayout }));

            string mapPath = Path.Combine(dir, "map.json");
            int firstBake = Program.Main(new[]
            {
                "bake", "--layout", firstLayout, "--map", mapPath,
                "--origin-x", "0", "--origin-z", "0", "--base-y", "0",
            });
            Assert.Equal(0, firstBake);

            int placementsAfterFirst = MapDocumentFile.Load(mapPath).Placements.Count;

            int secondBake = Program.Main(new[]
            {
                "bake", "--layout", secondLayout, "--map", mapPath,
                "--origin-x", "300", "--origin-z", "0", "--base-y", "0",
            });
            Assert.Equal(0, secondBake);

            MapDocument doc = MapDocumentFile.Load(mapPath);
            Assert.True(doc.Placements.Count > placementsAfterFirst,
                "second bake must append placements to the existing document");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MalformedConfig_Exits3_WithMessageNotStackTrace()
    {
        string dir = NewTempDir();
        TextWriter originalError = Console.Error;
        try
        {
            string configPath = Path.Combine(dir, "config.json");
            File.WriteAllText(configPath, "{ not valid json !");
            string layoutPath = Path.Combine(dir, "layout.json");

            using var stderr = new StringWriter();
            Console.SetError(stderr);

            int exit = Program.Main(new[]
            {
                "generate", "--seed", "1", "--config", configPath, "--out", layoutPath,
            });

            Assert.Equal(3, exit);
            string message = stderr.ToString();
            Assert.Contains("config", message, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", message, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UnknownVerb_Exits2()
    {
        int exit = Program.Main(new[] { "frobnicate" });

        Assert.Equal(2, exit);
    }

    [Fact]
    public void MissingRequiredArg_Exits2()
    {
        string dir = NewTempDir();
        try
        {
            string layoutPath = Path.Combine(dir, "layout.json");

            int exit = Program.Main(new[] { "generate", "--out", layoutPath });

            Assert.Equal(2, exit);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
