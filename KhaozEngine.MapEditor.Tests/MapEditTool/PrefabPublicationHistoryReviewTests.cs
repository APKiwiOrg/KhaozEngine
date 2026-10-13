using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class PrefabPublicationHistoryReviewTests
{
    [Fact]
    public void PublishedVersionPlacement_TwoUndosAndRedosKeepExactClosuresAndReservationsAfterReload()
    {
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Create("roomless"));
        var original = Pins(session);
        var service = new PrefabService(session);
        var asset = service.Open("hut");
        var child = asset.Children.OfType<MapPrefabPlacementChild>().Single();
        var value = MapPrefabJson.Copy(child.Value); value.X = 3;
        service.Edit(child with { Value = value });
        var published = service.Save("hut", "published");
        var forward = Pins(session);
        Assert.NotEqual(original, forward);
        Check(session, forward, published.Resource.Id, true, null);
        new MutationService(session).PrefabApply(new MapPlacePrefab("b", published.Resource.Id, new(new(8, 0, 0), 0, 1)));
        var bindings = PrefabEditorFixtures.Document(session).Prefabs!.Instances.Single(i => i.Id == "b").Bindings;
        Check(session, forward, published.Resource.Id, true, false, bindings);
        Assert.NotNull(session.UndoNative());
        Check(session, forward, published.Resource.Id, true, true, bindings);
        Assert.NotNull(session.UndoNative());
        Check(session, original, published.Resource.Id, false, true, bindings);
        Assert.NotNull(session.RedoNative());
        Check(session, forward, published.Resource.Id, true, true, bindings);
        Assert.NotNull(session.RedoNative());
        Check(session, forward, published.Resource.Id, true, false, bindings);
        Assert.Equal(bindings.Entries, PrefabEditorFixtures.Document(session).Prefabs!.Instances.Single(i => i.Id == "b").Bindings.Entries);
    }
    static (string Roots, string Closure) Pins(MapEditSession session) => session.WithNativeAssets(c =>
        (string.Join("|", c.Document.NativeAssets.Select(r => r.ToString())), c.Assets.Hash));
    static void Check(MapEditSession session, (string Roots, string Closure) expected, string version, bool available, bool? removed,
        MapPrefabBindings? reservations = null)
    {
        Assert.Equal(expected, Pins(session));
        Assert.Equal(available, session.WithNativeAssets(c => c.Assets.TryGetResource(version, out _)));
        if (removed is { } tombstone)
        {
            var b = PrefabEditorFixtures.Document(session).Prefabs!.Instances.Single(i => i.Id == "b");
            Assert.Equal(tombstone, b.Removed); Assert.False(b.Unpacked); Assert.Equal(version, b.ResourceId);
            Assert.NotEmpty(b.Bindings.Entries);
            Assert.Equal(reservations!.Entries.Select(e => (e.Child, e.WorldId, e.NumericId)), b.Bindings.Entries.Select(e => (e.Child, e.WorldId, e.NumericId)));
        }
        session.Save();
        var reload = new MapEditSession(); reload.Open(session.DocumentPath!);
        Assert.Equal(expected, Pins(reload));
        Assert.Equal(PrefabEditorFixtures.State(session).Identity, PrefabEditorFixtures.State(reload).Identity);
        Assert.Equal(PrefabEditorFixtures.State(session).Bindings, PrefabEditorFixtures.State(reload).Bindings);
    }
}
