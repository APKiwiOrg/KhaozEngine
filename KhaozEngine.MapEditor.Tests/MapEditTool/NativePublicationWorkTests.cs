using System;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

/// <summary>Complete effective expansions are the dominant cost of a native call, so each call's count is bounded.</summary>
public sealed class NativePublicationWorkTests
{
    /// <summary>Undo or redo of an unpack expands the open document and its staged candidate once each, shared by
    /// validation, effects and publication, plus the prefab-free projection that sizes the leaves the candidate keeps
    /// inside the prefab.</summary>
    [Fact]
    public void UndoAndRedo_ExpandEachDistinctDocumentOnce()
    {
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Village());
        new MutationService(session).PrefabApply(new MapUnpackPrefab("a"));
        Assert.Equal(3, Expansions(() => Assert.NotNull(session.UndoNative())));
        Assert.Equal(3, Expansions(() => Assert.NotNull(session.RedoNative())));
    }

    /// <summary>A local asset edit prepares a place and an unpack over the session's fixed context, two expansions each,
    /// stages the snapshot over two documents and exports once. The context and the prior export are not rebuilt.</summary>
    [Fact]
    public void LocalAssetEdit_ReusesContextAndExport()
    {
        var service = new PrefabService(PrefabEditorFixtures.Session(PrefabFixtures.Village()));
        var marker = service.Open("hut").Children.OfType<MapPrefabMarkerChild>().Single();
        Assert.Equal(7, Expansions(() => service.Edit(marker with { Value = marker.Value with { Name = "First" } })));
        Assert.Equal(7, Expansions(() => service.Edit(marker with { Value = marker.Value with { Name = "Second" } })));
        Assert.Equal(0, Expansions(() => service.Export()));
    }

    static long Expansions(Action action)
    {
        long before = MapPrefabResolver.ThreadExpansions;
        action();
        return MapPrefabResolver.ThreadExpansions - before;
    }
}
