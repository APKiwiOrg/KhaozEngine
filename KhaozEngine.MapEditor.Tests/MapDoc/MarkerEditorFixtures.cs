using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.Tests.MapDoc.Markers;
using KhaozEngine.Tests.MapDoc.Prefabs;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>Editor-only marker session fixtures over the public invented marker documents.</summary>
internal static class MarkerEditorFixtures
{
    /// <summary>A session opened from a saved copy of <paramref name="fixture"/>, with its closure written beside it.</summary>
    internal static MapEditSession Session(MarkerFixture fixture) =>
        PrefabEditorFixtures.Session(new PrefabFixture(fixture.Document, fixture.Assets));

    /// <summary>A canonical snapshot of the document, identity, closure, history labels and cursor, events and dirty
    /// state, so a rejected command can be shown to write nothing.</summary>
    internal static string State(MapEditSession session)
    {
        var (undo, redo) = session.NativeHistoryLabels;
        return string.Join("\n", PrefabEditorFixtures.State(session).ToString(), "undo=" + undo, "redo=" + redo);
    }

    /// <summary>The canonical text of the session's open document.</summary>
    internal static string Bytes(MapEditSession session) => PrefabEditorFixtures.State(session).DocumentBytes;

    /// <summary>The effective markers of the session's open document, in ordinal ID order.</summary>
    internal static MapEffectiveMarker[] Markers(MapEditSession session) =>
        session.WithNativeAssets(c => MapEffectiveAnnotations.Markers(MapPrefabResolver.Expand(c.Document, c.Assets)).ToArray());

    /// <summary>A second payload-1 world point beside the basic marker.</summary>
    internal static MapLocalMarkerDoc Other() =>
        new(1, "other", "other-name", new(3, 1, 3), 0, "entry-role", true, new[] { "other" });

    /// <summary>The basic world point and <see cref="Other"/>, so a rename can collide.</summary>
    internal static MarkerFixture TwoPoints() =>
        MarkerFixtures.WithWorldMarkers(MarkerFixtures.Create(), MarkerFixtures.BasicMarker(), Other());

    /// <summary>The world-space marker entry that expansion publishes for <paramref name="id"/>, the form marker_set
    /// accepts.</summary>
    internal static MapLocalMarkerDoc WorldEntry(MapDocument document, MapAssetClosure assets, string id) =>
        MapPrefabResolver.Expand(document, assets).BuildingIntent.Markers.Single(m => m.Id == id);
}
