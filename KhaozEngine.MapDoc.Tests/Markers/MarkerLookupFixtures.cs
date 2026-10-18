using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Markers;

/// <summary>Invented name-collision and rectangle fixtures. Linked into other test projects, so nothing here reads
/// files.</summary>
internal static partial class MarkerFixtures
{
    internal const string DuplicateResource = "duplicate-marker";
    internal static readonly MapPrefabChildKey EntryKey = new(MapPrefabChildKind.Marker, "entry");

    /// <summary>One collision between a prefab child and a world marker, both named <c>arrival</c>. In
    /// <c>disabled-child</c> the child <c>entry</c> of instance <c>a</c> is disabled and world marker <c>w</c> is enabled.
    /// In <c>disabled-world</c> the child is enabled and <c>w</c> is disabled, with role <c>unregistered-role</c> and tags
    /// second, first. No other marker exists.</summary>
    internal static MarkerFixture DuplicateNames(string variant)
    {
        bool childEnabled = variant switch
        {
            "disabled-child" => false,
            "disabled-world" => true,
            _ => throw new ArgumentException("Unknown duplicate-name variant " + variant),
        };
        var asset = new MapPrefabAsset(1, DuplicateResource, new MapPrefabChild[]
        {
            new MapPrefabMarkerChild(EntryKey, new(1, "entry", "arrival", new Vector3(1, 2, -1), 0.371f, "entry-role", childEnabled,
                new[] { "first", "second" })),
        });
        var assets = PrefabClosure(asset);
        MapDocument document = MapDocumentFile.LoadText(FormatSevenJson(false));
        document.NativeAssets = assets.Roots.ToList();
        document.BuildingIntent = new(1, [], [], [], new[] { DuplicateWorldMarker(variant) });
        document.Prefabs = new(1, 1, new[]
        {
            new MapPrefabInstance("a", DuplicateResource, assets.GetResource(DuplicateResource).Reference.Sha256, new(Vector3.Zero, 0, 1), null,
                new(new[] { new MapPrefabBinding(EntryKey, "a/entry", null, false) }), []),
        }, []);
        return new(document, assets);
    }

    /// <summary>The world participant of <see cref="DuplicateNames"/>, with its original ID and name.</summary>
    internal static MapLocalMarkerDoc DuplicateWorldMarker(string variant) => variant == "disabled-world"
        ? new(1, "w", "arrival", new Vector3(-0.25f, 12.25f, -1.5f), 0, "unregistered-role", false, new[] { "second", "first" })
        : new(1, "w", "arrival", new Vector3(-0.25f, 12.25f, -1.5f), 0, "entry-role", true, new[] { "first", "second" });

    /// <summary>A detached copy whose instance <c>a</c> carries exactly the given entry override, or none. A remove
    /// override marks the entry binding Removed, as the binding rules require. IDs never change.</summary>
    internal static MarkerFixture WithEntryOverride(MarkerFixture fixture, MapPrefabOverride? value)
    {
        MapDocument document = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document));
        var instance = document.Prefabs!.Instances.Single(i => i.Id == "a");
        document.Prefabs = new(1, 1, new[]
        {
            instance with
            {
                Overrides = value is null ? [] : new[] { value },
                Bindings = new(instance.Bindings.Entries.Select(b => b with { Removed = value is MapPrefabRemoveOverride }).ToArray()),
            },
        }, []);
        return fixture with { Document = document };
    }

    /// <summary>A detached copy whose world markers are exactly <paramref name="markers"/>.</summary>
    internal static MarkerFixture WithWorldMarkers(MarkerFixture fixture, params MapLocalMarkerDoc[] markers)
    {
        MapDocument document = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document));
        document.BuildingIntent = new(1, [], [], [], markers);
        return fixture with { Document = document };
    }

    /// <summary>Five payload-1 world points around the origin, IDs deliberately not in coordinate order: e (-1, -1),
    /// d (0, -1), c (-1, 0), b (0, 0) and a (-0.25, -0.5).</summary>
    internal static MarkerFixture RectPoints()
    {
        MarkerFixture fixture = Create();
        fixture.Document.BuildingIntent = new(1, [], [], [], new[]
        {
            RectPoint("e", -1, -1), RectPoint("d", 0, -1), RectPoint("c", -1, 0), RectPoint("b", 0, 0), RectPoint("a", -0.25f, -0.5f),
        });
        return fixture;
    }

    static MapLocalMarkerDoc RectPoint(string id, float x, float z) =>
        new(1, id, id + "-name", new Vector3(x, 1.5f, z), 0, "entry-role", true, new[] { "point" });
}

/// <summary>Counts patch reads and patch searches through a wrapped surface source, including those made inside bounded
/// acquisition sessions, which support queries use.</summary>
internal sealed class CountingSurfaceSource(IMapSurfaceSource inner) : IMapSurfaceAcquisitionSource
{
    public int ReadPatchCalls { get; private set; }
    public int FindPatchesCalls { get; private set; }
    public string SnapshotId => inner.SnapshotId;
    public string RootSha256 => inner.RootSha256;
    public IReadOnlyList<MapSurfaceRef> Surfaces => inner.Surfaces;

    public MapPatchRead ReadPatch(MapPatchKey key)
    {
        ReadPatchCalls++;
        return inner.ReadPatch(key);
    }

    public MapPatchFindResult FindPatches(MapSurfaceScope scope)
    {
        FindPatchesCalls++;
        return inner.FindPatches(scope);
    }

    public IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope) =>
        new Session(this, ((IMapSurfaceAcquisitionSource)inner).OpenAcquisition(scope));

    sealed class Session(CountingSurfaceSource owner, IMapSurfaceAcquisitionSession session) : IMapSurfaceAcquisitionSession
    {
        public string SnapshotId => session.SnapshotId;
        public string RootSha256 => session.RootSha256;
        public int PagesRead => session.PagesRead;
        public IReadOnlyList<MapPatchKey> ReservedPatchKeys => session.ReservedPatchKeys;
        public bool TryGetSurface(string surfaceId, out MapSurfaceRef? surface) => session.TryGetSurface(surfaceId, out surface);
        public bool TryGetIncidentRecords(MapPatchKey acquiredKey, out IReadOnlyList<MapRecordRef>? records) =>
            session.TryGetIncidentRecords(acquiredKey, out records);
        public void Dispose() => session.Dispose();

        public MapPatchFindResult FindPatches()
        {
            owner.FindPatchesCalls++;
            return session.FindPatches();
        }

        public MapPatchRead ReadPatch(MapPatchKey key)
        {
            owner.ReadPatchCalls++;
            return session.ReadPatch(key);
        }
    }
}
