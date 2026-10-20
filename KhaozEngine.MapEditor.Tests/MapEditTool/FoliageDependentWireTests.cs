using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Foliage;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

/// <summary>The public finalizer, the bound editor and the released wire verbs publish the same foliage rows for every
/// dependent edit, each beside its own base effects.</summary>
[Collection(McpWireCollection.Name)]
public sealed class FoliageDependentWireTests
{
    [Fact]
    public async Task DocumentFinalizer_PureEditorWireEffectsAgree()
    {
        FoliageFixture f = FoliageFixtures.Dependents();
        var (gui, history) = FoliageEditorFixtures.Gui(f);
        MapDocument pureDocument = MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document));
        MapEditSession seed = FoliageEditorFixtures.Session(f);
        try
        {
            await using var harness = await McpWire.StartAsync();
            await Call(harness, "map_open", new() { ["path"] = seed.DocumentPath });
            var live = harness.Services.GetRequiredService<MapEditSession>();

            // A dependent unframed standalone move: the raw candidate, its placement writes and the editor's own base
            // bounds finalize publicly to exactly the editor's and the wire's effects.
            history.Execute(gui, new MovePlacementCommand(FoliageFixtures.ShrubId, 8, -10, null));
            MapNativeEditEffects editorEffects = history.LastNativeEffects!;
            await Call(harness, "placement_move", new() { ["id"] = FoliageFixtures.ShrubId, ["x"] = 8f, ["z"] = -10f });
            MapNativeEditEffects wireEffects = live.LastNativeEffects!;
            MapDocument candidate = MapDocumentFile.LoadText(MapDocumentFile.SaveText(pureDocument));
            candidate.Placements.Single(p => p.Id == FoliageFixtures.ShrubId).X = 8;
            MapNativeEditEffects baseEffects = editorEffects with
            {
                FoliageChunks = [],
                FoliageLayers = [],
                Invalidates = editorEffects.Invalidates & ~MapNativeInvalidation.Foliage,
            };
            MapNativeInvalidation baseGeometryFlags = baseEffects.Invalidates;
            MapPrefabEditResult finalized = MapDocumentEditFinalizer.Finalize(pureDocument,
                new(candidate, MapNativeWriteSet.PlacementsOnly, baseEffects), f.Assets);
            Assert.Equal(baseGeometryFlags, finalized.Effects.Invalidates & ~MapNativeInvalidation.Foliage);
            Assert.True(baseGeometryFlags.HasFlag(MapNativeInvalidation.Physics | MapNativeInvalidation.Nav));
            Assert.Equal(editorEffects.Describe(), finalized.Effects.Describe());
            AssertAgree(finalized, editorEffects, wireEffects, dependent: true);
            pureDocument = finalized.After;
            AssertSameDocument(gui, live, pureDocument);

            // The released room, roof-link and marker verbs. Each path keeps its own base effects, the rows agree.
            MapPrismDomain room = FoliageFixtures.DependentRoom(gui) with { UpperY = 2.5f };
            MapRoofLinkDoc link = FoliageFixtures.DependentRoofLink();
            MapLocalMarkerDoc post = FoliageFixtures.MovedMarker(FoliageFixtures.DependentMarker(gui, FoliageFixtures.PostId), 4);
            MapLocalMarkerDoc far = FoliageFixtures.MovedMarker(FoliageFixtures.DependentMarker(gui, FoliageFixtures.FarId), 13);
            var steps = new (Func<MapDocument, MapPrefabEditResult> Pure, EditorCommand Editor, string Verb, Dictionary<string, object?> Args, bool Dependent)[]
            {
                (d => MapInteriorEdits.Prepare(d, f.Assets, new MapSetInterior(room)), new InteriorEditCommand(new MapSetInterior(room)),
                    "interior_volume_set", new() { ["domainJson"] = Encoding.UTF8.GetString(MapPrefabJson.Bytes<MapTopologyRecord>(room)) }, true),
                (d => MapInteriorEdits.Prepare(d, f.Assets, new MapSetRoofLink(link)), new InteriorEditCommand(new MapSetRoofLink(link)),
                    "roof_link_set", new() { ["linkJson"] = Encoding.UTF8.GetString(MapPrefabJson.Bytes(link)) }, false),
                (d => MapMarkerEdits.Prepare(d, f.Assets, new MapSetMarker(post)), new MarkerEditCommand(new MapSetMarker(post)),
                    "marker_set", new() { ["markerJson"] = Encoding.UTF8.GetString(MapPrefabJson.Bytes(post)) }, true),
                (d => MapMarkerEdits.Prepare(d, f.Assets, new MapSetMarker(far)), new MarkerEditCommand(new MapSetMarker(far)),
                    "marker_set", new() { ["markerJson"] = Encoding.UTF8.GetString(MapPrefabJson.Bytes(far)) }, false),
            };
            foreach (var (pure, editor, verb, args, dependent) in steps)
            {
                MapPrefabEditResult result = pure(pureDocument);
                history.Execute(gui, editor);
                await Call(harness, verb, args);
                AssertAgree(result, history.LastNativeEffects!, live.LastNativeEffects!, dependent);
                pureDocument = result.After;
                AssertSameDocument(gui, live, pureDocument);
            }
        }
        finally { FoliageEditorFixtures.Delete(seed); }
    }

    static void AssertAgree(MapPrefabEditResult pure, MapNativeEditEffects editorEffects, MapNativeEditEffects wireEffects, bool dependent)
    {
        Assert.Equal(pure.Effects.FoliageChunks, editorEffects.FoliageChunks);
        Assert.Equal(editorEffects.FoliageChunks, wireEffects.FoliageChunks);
        Assert.Equal(FoliageEditorFixtures.Channel(pure.Effects), FoliageEditorFixtures.Channel(editorEffects));
        Assert.Equal(FoliageEditorFixtures.Channel(editorEffects), FoliageEditorFixtures.Channel(wireEffects));
        // No dependent edit here is whole-layer, and the roof link reports equal empty rows and entries and no flag.
        Assert.Empty(pure.Effects.FoliageLayers);
        Assert.Equal(editorEffects.Describe(), wireEffects.Describe());
        foreach (MapNativeEditEffects effects in new[] { pure.Effects, editorEffects, wireEffects })
            Assert.Equal(dependent, effects.Invalidates.HasFlag(MapNativeInvalidation.Foliage));
        Assert.Equal(dependent, pure.Effects.FoliageChunks.Count != 0);
        // A dependent row keeps both sides' bounds and changes its dependency digest.
        Assert.All(pure.Effects.FoliageChunks, r => Assert.True(r.OldBounds is not null && r.NewBounds is not null && r.BeforeDigest != r.AfterDigest));
    }

    static void AssertSameDocument(MapDocument gui, MapEditSession live, MapDocument pure)
    {
        string bytes = MapDocumentFile.SaveText(gui);
        Assert.Equal(bytes, FoliageEditorFixtures.Bytes(live));
        Assert.Equal(MapFoliageProjection.Digest(gui.Foliage), MapFoliageProjection.Digest(pure.Foliage));
        Assert.Equal(gui.Placements.Select(p => (p.Id, p.X, p.Z)), pure.Placements.Select(p => (p.Id, p.X, p.Z)));
    }

    static async Task<CallToolResult> Call(McpAdapterTests.McpHarness harness, string name, Dictionary<string, object?> args)
    {
        CallToolResult result = await harness.Client.CallAsync(name, args);
        Assert.False(result.IsError == true, name + ": " + string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
        return result;
    }
}
