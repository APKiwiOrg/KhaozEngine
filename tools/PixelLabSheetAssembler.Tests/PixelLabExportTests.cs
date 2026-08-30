using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class PixelLabExportTests
{
    private static readonly string[] Dirs =
    {
        "south", "south-east", "east", "north-east", "north", "north-west", "west", "south-west",
    };

    // Build a synthetic export under a fresh temp dir; returns the root dir path.
    // "north-west" omits frame_002 (mid gap). All frames are 12x12.
    private static string WriteExport()
    {
        string root = Directory.CreateTempSubdirectory("pl_export_test_").FullName;
        string folder = "Hero";

        var dirJson = new List<string>();
        foreach (var d in Dirs)
        {
            var indices = d == "north-west" ? new[] { 0, 1 } : new[] { 0, 1, 2 };
            var paths = new List<string>();
            foreach (int i in indices)
            {
                string rel = $"{folder}/animations/walking/{d}/frame_{i:000}.png";
                string full = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using (var img = new Image<Rgba32>(12, 12)) { img[6, 11] = new Rgba32(255, 255, 255, 255); img.SaveAsPng(full); }
                paths.Add($"\"{rel}\"");
            }
            dirJson.Add($"\"{d}\": [{string.Join(",", paths)}]");
        }

        string meta = $@"{{
  ""states"": [
    {{
      ""character"": {{ ""name"": ""Hero"", ""size"": {{ ""width"": 12, ""height"": 12 }} }},
      ""folder"": ""{folder}"",
      ""frames"": {{ ""animations"": {{ ""walking"": {{ {string.Join(",", dirJson)} }} }} }}
    }}
  ]
}}";
        File.WriteAllText(Path.Combine(root, "metadata.json"), meta);
        return root;
    }

    [Fact]
    public void Loads_directory_export_and_reports_character_name()
    {
        string root = WriteExport();
        var (anim, temp) = PixelLabExport.Load(root, "walking");

        Assert.Null(temp); // a dir input is not extracted, so nothing to clean up
        Assert.Equal("Hero", anim.CharacterName);
        Assert.Equal("walking", anim.AnimName);
        Assert.Equal(8, anim.FramesByDir.Count);
    }

    [Fact]
    public void Parses_frame_indices_and_drops_the_gap()
    {
        string root = WriteExport();
        var (anim, _) = PixelLabExport.Load(root, "walking");

        var nw = anim.FramesByDir["north-west"].Select(f => f.Index).OrderBy(i => i).ToArray();
        Assert.Equal(new[] { 0, 1 }, nw); // frame_002 absent

        var south = anim.FramesByDir["south"].Select(f => f.Index).OrderBy(i => i).ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, south);
    }

    [Fact]
    public void Unknown_animation_lists_available_names()
    {
        string root = WriteExport();
        var ex = Assert.Throws<AssemblyException>(() => PixelLabExport.Load(root, "running"));
        Assert.Contains("walking", ex.Message);
    }

    // Zips a synthetic export and returns the .zip path (inside its own temp dir, so the caller can delete both).
    private static string WriteExportZip()
    {
        string source = WriteExport();
        string zip = Path.Combine(Directory.CreateTempSubdirectory("pl_zip_test_").FullName, "export.zip");
        ZipFile.CreateFromDirectory(source, zip);
        return zip;
    }

    private static string[] ExtractedTempDirs() =>
        Directory.GetDirectories(Path.GetTempPath(), "pixellab_*");

    [Fact]
    public void Loads_zip_export_and_hands_back_the_extracted_dir()
    {
        string zip = WriteExportZip();
        var (anim, temp) = PixelLabExport.Load(zip, "walking");

        Assert.NotNull(temp);
        Assert.True(Directory.Exists(temp));
        Assert.Equal("Hero", anim.CharacterName);
        Assert.Equal(8, anim.FramesByDir.Count);

        Directory.Delete(temp, recursive: true);   // the caller owns it on the success path
    }

    [Fact]
    public void Failed_zip_load_deletes_the_directory_it_extracted()
    {
        string zip = WriteExportZip();
        string[] before = ExtractedTempDirs();

        var ex = Assert.Throws<AssemblyException>(() => PixelLabExport.Load(zip, "running"));
        Assert.Contains("walking", ex.Message);    // the message that invites another attempt

        // Nothing new left behind: the throw happens after extraction, and the caller never got a path to delete.
        Assert.Empty(ExtractedTempDirs().Except(before));
    }

    [Fact]
    public void End_to_end_assembles_a_loaded_export()
    {
        string root = WriteExport();
        var (anim, _) = PixelLabExport.Load(root, "walking");
        var result = SheetAssembler.Assemble(anim, new AssemblyOptions());
        using var sheet = result.Sheet;

        Assert.Equal(3, result.FrameCount);
        Assert.Equal(12 * 3, sheet.Width);
        Assert.Equal(12 * 8, sheet.Height);
        Assert.Single(result.Warnings); // the north-west gap
    }
}
