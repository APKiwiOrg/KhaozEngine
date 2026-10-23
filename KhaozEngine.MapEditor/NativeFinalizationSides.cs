using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;

namespace KhaozEngine.MapEditor;

/// <summary>What one staged transaction may reuse from earlier finalizations: the command's own stamp for this direction
/// and the last accepted publication's after side. Each side is reused, with its expansion and context, only when the
/// live document or the staged publication still has its fingerprint and closure. Inactive when neither document holds
/// foliage or a closure is missing, so released transactions compute no fingerprint.</summary>
internal readonly record struct NativeFinalizationSides(bool Active, string? BeforeFingerprint, string? AfterFingerprint,
    MapDocumentFinalizationSide? Before, MapDocumentFinalizationSide? After, MapDocumentFinalizationStamp? Stamp,
    MapDocumentFinalizationSide? Accepted)
{
    internal static NativeFinalizationSides Of(MapDocument document, MapDocument publication, MapAssetClosure? beforeAssets,
        MapAssetClosure? afterAssets, MapDocumentFinalizationStamp? stamp, MapDocRegistry registry)
    {
        if (beforeAssets is null || afterAssets is null || document.Foliage is null && publication.Foliage is null) return default;
        string fa = MapDocumentEditFinalizer.Fingerprint(document, registry), fb = MapDocumentEditFinalizer.Fingerprint(publication, registry);
        MapDocumentFinalizationSide? accepted = NativeAcceptedFinalizations.Before(document);
        MapDocumentFinalizationSide? before = stamp?.Before.Matches(fa, beforeAssets) == true ? stamp.Before
            : accepted?.Matches(fa, beforeAssets) == true ? accepted : null;
        MapDocumentFinalizationSide? after = stamp?.After.Matches(fb, afterAssets) == true ? stamp.After : null;
        return new(true, fa, fb, before, after, stamp, accepted);
    }
}

/// <summary>The after side of the finalization each live document last accepted, so the next transaction on that
/// document reuses its captured context. A document whose publication carried no finalization forgets it.</summary>
internal static class NativeAcceptedFinalizations
{
    static readonly ConditionalWeakTable<MapDocument, MapDocumentFinalizationSide> s_sides = new();

    internal static void Remember(MapDocument live, MapDocumentFinalizationStamp? stamp)
    {
        if (stamp is null) s_sides.Remove(live);
        else s_sides.AddOrUpdate(live, stamp.After);
    }

    internal static MapDocumentFinalizationSide? Before(MapDocument live) => s_sides.TryGetValue(live, out var side) ? side : null;
}
