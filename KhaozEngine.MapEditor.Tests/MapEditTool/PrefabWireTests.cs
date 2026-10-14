using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEdit.Tools;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Prefabs;
using ModelContextProtocol;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

[Collection(McpWireCollection.Name)]
public sealed class PrefabWireTests
{
    [Theory]
    [InlineData("place")]
    [InlineData("override")]
    [InlineData("unpack")]
    [InlineData("undo")]
    [InlineData("redo")]
    public void GuiServiceAndWire_HaveIdenticalDocumentsEffectsAndRejection(string verb)
    {
        var f = PrefabFixtures.Create("roomless");
        var direct = PrefabEditorFixtures.Session(f); var wire = PrefabEditorFixtures.Session(f);
        var mutation = new MutationService(direct);
        var tools = new PrefabTools(new PrefabService(wire), new MutationService(wire), wire);
        var change = new MapPrefabPlacementOverride(new(MapPrefabChildKind.Placement, "item"), new(new(2, 0, -2), 0.371f, 0.8f), null, null);
        if (verb is "undo" or "redo")
        {
            mutation.PrefabApply(new MapSetPrefabOverride("a", change));
            tools.PrefabOverride("a", System.Text.Encoding.UTF8.GetString(MapPrefabJson.Bytes<MapPrefabOverride>(change)));
            if (verb == "redo") { direct.UndoNative(); tools.PrefabUndo(); }
        }
        MapNativeEditEffects? a = verb switch
        {
            "place" => mutation.PrefabApply(new MapPlacePrefab("new", "hut", new(new(10.25f, 5, -3.125f), 0.371f, 1.2f))).Effects,
            "override" => mutation.PrefabApply(new MapSetPrefabOverride("a", change)).Effects,
            "unpack" => mutation.PrefabApply(new MapUnpackPrefab("a")).Effects,
            "undo" => direct.UndoNative(),
            _ => direct.RedoNative(),
        };
        MapNativeEditEffects? b = verb switch
        {
            "place" => tools.PrefabPlace("new", "hut", 10.25f, 5, -3.125f, 0.371f, 1.2f).Effects,
            "override" => tools.PrefabOverride("a", System.Text.Encoding.UTF8.GetString(MapPrefabJson.Bytes<MapPrefabOverride>(change))).Effects,
            "unpack" => tools.PrefabUnpack("a").Effects,
            "undo" => tools.PrefabUndo(),
            _ => tools.PrefabRedo(),
        };
        Assert.Equal(PrefabEditorFixtures.State(direct).DocumentBytes, PrefabEditorFixtures.State(wire).DocumentBytes);
        Assert.Equal(a!.Describe(), b!.Describe());
        var before = PrefabEditorFixtures.State(wire);
        Assert.Throws<McpException>(() => tools.PrefabPlace("bad", "hut", 0, 0, 0, 0, 0));
        Assert.Equal(before, PrefabEditorFixtures.State(wire));
    }
    [Fact]
    public void WireSaveOpen_UsesTheSameCapturedLocalSession()
    {
        var session = PrefabEditorFixtures.Session(PrefabEditorFixtures.LocalCapturedFixture());
        var tools = new PrefabTools(new PrefabService(session), new MutationService(session), session);
        var asset = tools.PrefabOpen("captured");
        var light = asset.Children.OfType<MapPrefabLightChild>().Single();
        tools.PrefabEdit(System.Text.Encoding.UTF8.GetString(MapPrefabJson.Bytes<MapPrefabChild>(light with { Value = light.Value with { Radius = 4, Position = new(0, 2.5f, 0) } })));
        var saved = tools.PrefabSave("captured", "published");
        var reopened = tools.PrefabOpen(saved.Resource.Id);
        Assert.Equal(4, reopened.Children.OfType<MapPrefabLightChild>().Single().Value.Radius);
        Assert.Equal(2, reopened.MaterializedAttachments.Count);
        Assert.Contains(tools.PrefabList(), i => i.ResourceId == saved.Resource.Id);
    }
    [Theory]
    [InlineData("open")]
    [InlineData("edit")]
    [InlineData("save")]
    [InlineData("extract")]
    [InlineData("list")]
    public void GuiServiceAndWire_HaveIdenticalAssetSessionsAndRejection(string verb)
    {
        var f = PrefabFixtures.Create("roomless");
        var a = PrefabEditorFixtures.Session(f); var b = PrefabEditorFixtures.Session(f);
        var direct = new PrefabService(a); var wire = new PrefabTools(new PrefabService(b), new MutationService(b), b);
        var child = f.Assets.GetPrefab("hut").Children.OfType<MapPrefabPlacementChild>().Single();
        var value = MapPrefabJson.Copy(child.Value); value.X = 3;
        child = child with { Value = value };
        MapPrefabAsset? left = null, right = null;
        if (verb == "list") Assert.Equal(direct.List(), wire.PrefabList());
        else if (verb == "extract")
        {
            var selected = new[] { new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "a/item") };
            left = direct.Extract(selected, new(Vector3.Zero, 0, 1), "extracted");
            right = wire.PrefabExtract(Encoding.UTF8.GetString(MapPrefabJson.Bytes(selected)), 0, 0, 0, 0, 1, "extracted");
        }
        else
        {
            left = direct.Open("hut"); right = wire.PrefabOpen("hut");
            if (verb is "edit" or "save")
            {
                left = direct.Edit(child); right = wire.PrefabEdit(Encoding.UTF8.GetString(MapPrefabJson.Bytes<MapPrefabChild>(child)));
            }
            if (verb == "save")
            {
                var savedA = direct.Save("hut", "published"); var savedB = wire.PrefabSave("hut", "published");
                Assert.Equal(savedA.Resource, savedB.Resource);
                Assert.Equal(savedA.Mutation!.Effects.Describe(), savedB.Mutation!.Effects.Describe());
            }
        }
        if (left is not null) Assert.Equal(MapPrefabFile.Write(left), MapPrefabFile.Write(right!));
        Assert.Equal(PrefabEditorFixtures.State(a).DocumentBytes, PrefabEditorFixtures.State(b).DocumentBytes);
        var beforeA = PrefabEditorFixtures.State(a); var beforeB = PrefabEditorFixtures.State(b);
        Assert.Throws<MapDocumentException>(() => direct.Open("missing"));
        Assert.Throws<McpException>(() => wire.PrefabOpen("missing"));
        Assert.Equal(beforeA, PrefabEditorFixtures.State(a)); Assert.Equal(beforeB, PrefabEditorFixtures.State(b));
    }
    [Theory]
    [InlineData("add")]
    [InlineData("set")]
    [InlineData("remove")]
    [InlineData("roof")]
    public void GuiServiceAndWire_HaveIdenticalInteriorDocumentsEffectsAndRejection(string verb)
    {
        var f = verb == "add" ? MapInteriorCreationFixtures.Empty() : PrefabEditorFixtures.StandaloneInterior(true);
        var a = PrefabEditorFixtures.Session(f); var b = PrefabEditorFixtures.Session(f);
        var direct = new MutationService(a); var wire = new InteriorTools(new MutationService(b), b);
        var original = PrefabEditorFixtures.StandaloneInterior(true).Document.Surfaces.AllRecords().OfType<MapPrismDomain>().Single();
        var domain = verb == "add" ? MapInteriorCreationFixtures.Domain() : original with { UpperY = 2.5f };
        var roof = new MapRoofLinkDoc("link", new(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "a/roof"),
            new[] { new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Record, "a/room") }, [], 2.5f);
        MapPrefabMutationResult left = verb switch
        {
            "add" or "set" => direct.InteriorApply(new MapSetInterior(domain)),
            "remove" => direct.InteriorApply(new MapRemoveInterior("a/room")),
            _ => direct.InteriorApply(new MapSetRoofLink(roof)),
        };
        string json = Encoding.UTF8.GetString(MapPrefabJson.Bytes<MapTopologyRecord>(domain));
        MapPrefabMutationResult right = verb switch
        {
            "add" => wire.InteriorVolumeAdd(json),
            "set" => wire.InteriorVolumeSet(json),
            "remove" => wire.InteriorVolumeRemove("a/room"),
            _ => wire.RoofLinkSet(Encoding.UTF8.GetString(MapPrefabJson.Bytes(roof))),
        };
        Assert.Equal(PrefabEditorFixtures.State(a).DocumentBytes, PrefabEditorFixtures.State(b).DocumentBytes);
        Assert.Equal(left.Effects.Describe(), right.Effects.Describe());
        var before = PrefabEditorFixtures.State(b);
        Assert.Throws<McpException>(() => wire.InteriorVolumeSet(Encoding.UTF8.GetString(MapPrefabJson.Bytes<MapTopologyRecord>(domain with { Id = "missing" }))));
        Assert.Equal(before, PrefabEditorFixtures.State(b));
        Assert.Equal(PrefabEditorFixtures.State(a).DocumentBytes, PrefabEditorFixtures.State(b).DocumentBytes);
    }
    [Fact]
    public void InteriorAddAndSet_PreconditionsRefuseBeforePublication()
    {
        var f = MapInteriorCreationFixtures.Empty(); var session = PrefabEditorFixtures.Session(f);
        var tools = new InteriorTools(new MutationService(session), session);
        var domain = MapInteriorCreationFixtures.Domain();
        string json = Encoding.UTF8.GetString(MapPrefabJson.Bytes<MapTopologyRecord>(domain));
        var before = PrefabEditorFixtures.State(session);
        Assert.Throws<McpException>(() => tools.InteriorVolumeSet(json)); Assert.Equal(before, PrefabEditorFixtures.State(session));
        tools.InteriorVolumeAdd(json);
        var created = PrefabEditorFixtures.State(session);
        Assert.Throws<McpException>(() => tools.InteriorVolumeAdd(json));
        Assert.Throws<McpException>(() => tools.InteriorVolumeAdd(Encoding.UTF8.GetString(MapPrefabJson.Bytes<MapTopologyRecord>(domain with { Id = "different-volume" }))));
        Assert.Equal(created, PrefabEditorFixtures.State(session));
        tools.InteriorVolumeSet(Encoding.UTF8.GetString(MapPrefabJson.Bytes<MapTopologyRecord>(domain with { UpperY = 2.5f })));
        Assert.Equal((2, 2, 2L), session.NativeHistoryState);
    }
    [Fact]
    public async Task RealMcpWire_RoundTripsClosedChildrenCapturedRowsAndHistory()
    {
        var session = PrefabEditorFixtures.Session(PrefabEditorFixtures.LocalCapturedFixture());
        await using var harness = await McpWire.StartAsync();
        var opened = await harness.Client.CallAsync("map_open", new Dictionary<string, object?> { ["path"] = session.DocumentPath });
        Assert.NotEqual(true, opened.IsError);
        var result = await harness.Client.CallAsync("prefab_open", new Dictionary<string, object?> { ["resourceId"] = "captured" });
        Assert.NotEqual(true, result.IsError);
        string payload = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        var asset = MapPrefabFile.Read(Encoding.UTF8.GetBytes(payload), "captured");
        Assert.Equal(2, asset.MaterializedAttachments.Count);
        var light = asset.Children.OfType<MapPrefabLightChild>().Single();
        var edited = await harness.Client.CallAsync("prefab_edit", new Dictionary<string, object?>
        { ["childJson"] = Encoding.UTF8.GetString(MapPrefabJson.Bytes<MapPrefabChild>(light with { Value = light.Value with { Radius = 4, Position = new(0, 2.5f, 0) } })) });
        Assert.NotEqual(true, edited.IsError);
        var saved = await harness.Client.CallAsync("prefab_save", new Dictionary<string, object?> { ["resourceId"] = "captured", ["relativeDirectory"] = "published" });
        Assert.NotEqual(true, saved.IsError);
        using var savedJson = JsonDocument.Parse(Assert.Single(saved.Content.OfType<TextContentBlock>()).Text);
        string version = savedJson.RootElement.GetProperty("resource").GetProperty("id").GetString()!;
        var reopen = await harness.Client.CallAsync("prefab_open", new Dictionary<string, object?> { ["resourceId"] = version });
        Assert.NotEqual(true, reopen.IsError);
        var reopened = MapPrefabFile.Read(Encoding.UTF8.GetBytes(Assert.Single(reopen.Content.OfType<TextContentBlock>()).Text), "captured");
        Assert.Equal(4, reopened.Children.OfType<MapPrefabLightChild>().Single().Value.Radius);
        var live = harness.Services.GetRequiredService<MapEditSession>();
        Assert.Equal((1, 1, 1L), live.NativeHistoryState);
        Assert.NotEqual(true, (await harness.Client.CallAsync("prefab_undo", new Dictionary<string, object?>())).IsError);
        Assert.Equal((0, 1, 2L), live.NativeHistoryState);
        Assert.NotEqual(true, (await harness.Client.CallAsync("prefab_redo", new Dictionary<string, object?>())).IsError);
        Assert.Equal((1, 1, 3L), live.NativeHistoryState);
    }

}
