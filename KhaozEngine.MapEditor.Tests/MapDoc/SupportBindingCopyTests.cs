using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SupportBindingCopyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicatePreservesAuthoredSupportBinding(bool tool)
    {
        using var fixture = new NativePlacementHistoryFixture();
        MapDocument doc = tool ? fixture.SessionDocument : fixture.Document;
        MapSupportBinding binding = Configure(doc, fixture.Assets);
        string id;
        if (tool)
            id = fixture.Service.ElementDuplicate("placement", id: "existing").Id!;
        else
        {
            fixture.Editor.Selection.Set(SelectionKind.Placement, "existing");
            var controller = new EditorToolController(fixture.Editor);
            EditorToolController.DuplicateResult result = Assert.IsType<EditorToolController.DuplicateResult>(controller.DuplicateSelection());
            id = result.Id;
        }
        MapPlacement duplicate = doc.Placements.Single(p => p.Id == id);
        Assert.NotEqual("existing", id);
        Assert.Equal(binding, duplicate.SupportBinding);
        Assert.Equal(binding, doc.Placements.Single(p => p.Id == "existing").SupportBinding);
        Assert.Equal(11, duplicate.NumericId);
        Assert.Equal((2f, 2f), (duplicate.X, duplicate.Z));
        Assert.Null(duplicate.Y);
        if (!tool)
        {
            Assert.True(fixture.Editor.Undo());
            Assert.Single(doc.Placements);
            Assert.True(fixture.Editor.Redo());
            Assert.Equal(binding, doc.Placements.Single(p => p.Id == id).SupportBinding);
        }
    }

    static MapSupportBinding Configure(MapDocument doc, MapAssetClosure assets)
    {
        doc.ResolverIdentity = new(1, 2);
        doc.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2;
        var surface = new MapSurfaceRef("ground", MapLatticeFrame.ImportedMetreCentimetre,
            MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, "");
        surface = surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, Array.Empty<KeyValuePair<MapPatchKey, string>>()),
        };
        doc.Surfaces.Refs.Add(surface);
        var binding = new MapSupportBinding(MapSupportBindingKind.Surface, "ground", null, 0.25f, 1f, 2f);
        MapPlacement source = doc.Placements.Single(p => p.Id == "existing");
        source.Y = null;
        source.SupportBinding = binding;
        MapBoundDocumentValidation.Validate(doc, assets);
        return binding;
    }
}
