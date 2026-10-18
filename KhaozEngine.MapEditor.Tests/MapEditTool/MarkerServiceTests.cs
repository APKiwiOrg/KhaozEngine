using System;
using System.Linq;
using System.Numerics;
using System.Text;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEdit.Tools;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Markers;
using KhaozEngine.Tests.MapDoc.Prefabs;
using ModelContextProtocol;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

/// <summary>The marker service surface: the resolver prerequisite and the exact list filters.</summary>
public sealed class MarkerServiceTests
{
    [Fact]
    public void ResolverOneNative_MarkerVerbsNameResolverAdoption()
    {
        var (document, assets, _) = FormatFourFixtures.Build();
        Assert.Equal(new MapResolverIdentityDoc(1, 1), document.ResolverIdentity);
        MapEditSession session = PrefabEditorFixtures.Session(new PrefabFixture(document, assets));
        var tools = new MarkerTools(new MutationService(session), new QueryService(session));
        string bytes = MapDocumentFile.SaveText(PrefabEditorFixtures.Document(session));
        var history = session.NativeHistoryState;
        string json = Encoding.UTF8.GetString(MapPrefabJson.Bytes(MarkerFixtures.BasicMarker()));

        foreach (Func<MapPrefabMutationResult> verb in new Func<MapPrefabMutationResult>[] { () => tools.MarkerSet(json), () => tools.MarkerRemove("m") })
        {
            var refused = Assert.Throws<McpException>(verb);
            Assert.Contains("resolver adoption", refused.Message, StringComparison.Ordinal);
            Assert.Contains("MapResolverAdoption.ConvertToAuthoredSupport", refused.Message, StringComparison.Ordinal);
            Assert.Equal(bytes, MapDocumentFile.SaveText(PrefabEditorFixtures.Document(session)));
            Assert.Equal(history, session.NativeHistoryState);
            Assert.False(session.IsDirty);
        }
        // The resolver's expansion counter shows the empty list is the prerequisite's answer, not an empty expansion.
        long expansions = MapPrefabResolver.ThreadExpansions;
        Assert.Empty(tools.MarkerList());
        Assert.Empty(new QueryService(session).MarkerList(null, null, null, false, null));
        Assert.Empty(tools.MarkerList(minX: -1, minZ: -1, maxX: 1, maxZ: 1));
        Assert.Equal(expansions, MapPrefabResolver.ThreadExpansions);
        // Control: the same counter sees a resolver-2 list expand.
        MapEditSession adopted = MarkerEditorFixtures.Session(MarkerFixtures.Create());
        Assert.Single(new QueryService(adopted).MarkerList(null, null, null, false, null));
        Assert.NotEqual(expansions, MapPrefabResolver.ThreadExpansions);
    }

    [Fact]
    public void MarkerList_FiltersExactlyByNameRoleTagAndEnabled()
    {
        MapLocalMarkerDoc hidden = new(1, "x", "x-name", new Vector3(2, 1, 2), 0, "guard-role", false, new[] { "first" });
        MapLocalMarkerDoc third = new(1, "y", "y-name", new Vector3(3, 1, 3), 0, "entry-role", true, new[] { "third", "third" });
        MarkerFixture fixture = MarkerFixtures.WithWorldMarkers(MarkerFixtures.Create(), MarkerFixtures.BasicMarker(), hidden, third);
        MapEditSession session = MarkerEditorFixtures.Session(fixture);
        var query = new QueryService(session);
        var tools = new MarkerTools(new MutationService(session), query);
        string[] Ids(string? name = null, string? role = null, string? tag = null, bool enabledOnly = false) =>
            query.MarkerList(name, role, tag, enabledOnly, null).Select(m => m.Id).ToArray();

        Assert.Equal(new[] { "m", "x", "y" }, Ids());
        Assert.Equal(new[] { "m", "y" }, Ids(enabledOnly: true));
        Assert.Equal(new[] { "x" }, Ids(name: "x-name"));
        Assert.Empty(Ids(name: "x-name", enabledOnly: true));
        Assert.Empty(Ids(name: "X-name"));
        Assert.Equal(new[] { "m", "y" }, Ids(role: "entry-role"));
        Assert.Equal(new[] { "m", "x" }, Ids(tag: "first"));
        Assert.Equal(new[] { "y" }, Ids(tag: "third"));
        Assert.Equal(new[] { "m" }, Ids(tag: "first", enabledOnly: true));
        Assert.Equal(new[] { "m" }, Ids(name: "arrival", role: "entry-role", tag: "second"));
        Assert.Empty(Ids(name: "arrival", role: "guard-role"));

        // The rows are the effective markers, unchanged.
        Assert.Equal(MapPrefabJson.Bytes(MarkerEditorFixtures.Markers(session)), MapPrefabJson.Bytes(query.MarkerList(null, null, null, false, null).ToArray()));
        Assert.Equal(query.MarkerList("y-name", null, null, false, null).Select(m => m.Id), tools.MarkerList(name: "y-name").Select(m => m.Id));

        // The four rectangle parameters are all present or all absent.
        foreach (var partial in new Func<object>[] { () => tools.MarkerList(minX: -1), () => tools.MarkerList(minX: -1, minZ: -1, maxX: 0) })
        {
            var refused = Assert.Throws<McpException>(partial);
            Assert.Contains("minX, minZ, maxX and maxZ", refused.Message, StringComparison.Ordinal);
        }
        Assert.Equal(new[] { "m" }, tools.MarkerList(minX: -1, minZ: -2, maxX: 0, maxZ: 0).Select(m => m.Id));
    }
}
