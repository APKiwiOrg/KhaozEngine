using System;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

public sealed class ViewportWorldVisibilityTests
{
    [Fact]
    public void ExplicitCategoryClassifiesButManifestNameDoesNot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-visibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "trees.manifest.json");
        File.WriteAllText(path, "{ \"props\": [" +
            "{ \"id\": \"oak\", \"file\": \"oak.glb\", \"heightMeters\": 8, \"category\": \"trees\" }," +
            "{ \"id\": \"ruin-rock\", \"file\": \"rock.glb\", \"heightMeters\": 2 } ] }");
        try
        {
            var world = new ViewportWorld(null!, new[] { path });
            Assert.Equal(EditorPropCategory.Trees, world.PropCategoryOf("oak"));
            Assert.Equal(EditorPropCategory.OtherProps, world.PropCategoryOf("ruin-rock"));
            world.PropCategoryResolver = _ => EditorPropCategory.Rocks;
            Assert.Equal(EditorPropCategory.Rocks, world.PropCategoryOf("oak"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BuildPropLayers_RetainsAllLayersAndCompanionHostIdentity()
    {
        var doc = new MapDocument { Id = "visibility" };
        doc.ScatterLayers.Add(new MapScatterLayer { Name = "forest" });
        doc.ScatterLayers.Add(new MapScatterLayer { Name = "rocks" });
        doc.CompanionLayers.Add(new MapCompanionLayer { Name = "ferns", HostLayer = "forest" });
        var world = new ViewportWorld(null!, Array.Empty<string>())
        {
            ScatterLayerVisible = layer => layer != "forest",
        };

        var layers = world.BuildPropLayers(doc);

        Assert.Equal(new[] { "forest", "rocks", "forest" }, layers.Select(layer => layer.Identity).ToArray());
    }

    [Fact]
    public void DuplicateKit_FirstManifestWithoutCategoryStaysOtherProps()
    {
        string firstDir = Path.Combine(Path.GetTempPath(), "ke-category-first-" + Guid.NewGuid().ToString("N"));
        string secondDir = Path.Combine(Path.GetTempPath(), "ke-category-second-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(firstDir);
        Directory.CreateDirectory(secondDir);
        string first = Path.Combine(firstDir, "props.manifest.json");
        string second = Path.Combine(secondDir, "trees.manifest.json");
        File.WriteAllText(first,
            "{ \"props\": [{ \"id\": \"oak\", \"file\": \"first.glb\", \"heightMeters\": 2 }] }");
        File.WriteAllText(second,
            "{ \"props\": [{ \"id\": \"oak\", \"file\": \"second.glb\", \"heightMeters\": 9, " +
            "\"category\": \"trees\" }] }");
        try
        {
            var world = new ViewportWorld(null!, new[] { first, second });
            var visibility = new EditorVisibility();
            visibility.SetCategory(EditorPropCategory.Trees, false);

            Assert.Equal(2f, world.KindHeights["oak"]);
            Assert.Equal(EditorPropCategory.OtherProps, world.PropCategoryOf("oak"));
            Assert.True(visibility.GetCategory(world.PropCategoryOf("oak")));
        }
        finally
        {
            Directory.Delete(firstDir, true);
            Directory.Delete(secondDir, true);
        }
    }
}
