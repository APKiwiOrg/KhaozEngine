using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Expands persisted identities through one captured effective resource snapshot.</summary>
public static class MapPrefabResolver
{
    [ThreadStatic] static long t_expansions;

    /// <summary>Complete expansions started on the calling thread. Bounds the expansion work of one synchronous operation.</summary>
    internal static long ThreadExpansions => t_expansions;

    public static MapEffectiveResources Expand(MapDocument document, MapAssetClosure assets, MapPrefabExpansionLimits? limits = null) =>
        Expand(document, assets, limits ?? new(), null);

    internal static MapEffectiveResources Expand(MapDocument document, MapAssetClosure assets, MapPrefabExpansionLimits limits,
        MapPrefabExpansionObserver? observer, IMapSurfaceSource? nativeSource = null)
    {
        t_expansions++;
        _ = MapPrefabPreflight.Count(document, assets, limits);
        observer?.EnterStage("Copies");
        MapPrefabIdentity.RequireOwnership(document, assets);
        if (document.Tiles is { IsPartial: true }) throw new MapDocumentException("Effective expansion requires a complete document.");
        var numericErrors = new List<string>();
        _ = MapNativeValidation.ValidateNumericIds(document, numericErrors);
        if (numericErrors.Count != 0) throw new MapDocumentException(string.Join("\n", numericErrors));
        // Expansion never reads foliage, so the authored copy shares it rather than encoding the density payload.
        var authored = MapDocumentFile.CopyWithSharedFoliage(document);
        var product = MapEffectiveResolutionKernel.Resolve(MapEffectiveResolutionInput.FromDocument(authored, MapSurfaceSemantics.RootDigest(authored)), assets, nativeSource, observer);
        var resources = new MapEffectiveResources(product)
        {
            ClosureHash = assets.Hash,
            SourceFingerprint = document.Foliage is null ? null : Editing.MapDocumentEditFinalizer.BaseFingerprint(document),
        };
        var foliageErrors = Foliage.MapFoliageReferences.ValidateOwners(authored, resources);
        if (foliageErrors.Count != 0) throw new MapDocumentException(string.Join("\n", foliageErrors));
        return resources;
    }

    /// <summary>F-R7. The one frame choice an owner placement declares in an instance, shared by expansion and
    /// extraction. A materialized owner declares its captured placement frame. Any other owner composes its local
    /// transform and nullable-Y placement pose. Support requests are relative to this pre-seat frame.</summary>
    internal static MapEffectiveTransform DeclaredOwnerFrame(MapEffectiveTransform instancePose, MapTransform localTransform,
        MapPlacement placement, MapEffectiveTransform? capturedPlacementFrame) => capturedPlacementFrame is { } captured
        ? MapEffectiveTransform.Compose(instancePose, captured)
        : MapEffectiveTransform.Compose(MapEffectiveTransform.Compose(instancePose, localTransform), MapPrefabMaterializationOverrides.Pose(placement));

    /// <summary>The prefab-local form of a world-space support request in a declared pre-seat frame.</summary>
    internal static MapSupportBinding LocalRequest(MapSupportBinding binding, MapEffectiveTransform frame) => binding with
    {
        ReferenceY = binding.ReferenceY is { } y ? (float)((y - frame.Y) / frame.Scale) : null,
        SearchBelow = binding.SearchBelow is { } below ? (float)(below / frame.Scale) : null,
        SearchAbove = binding.SearchAbove is { } above ? (float)(above / frame.Scale) : null,
    };

    /// <summary>The world-space form of a prefab-local support request in its placement's pre-seat frame.</summary>
    internal static MapSupportBinding WorldRequest(MapSupportBinding binding, MapEffectiveTransform pose) => binding with
    {
        ReferenceY = binding.ReferenceY is { } y ? new MapResourceTransform(pose).Height(MapExactValue.FromSingle(y)).ToSingle() : null,
        SearchBelow = binding.SearchBelow is { } below ? (float)(below * pose.Scale) : null,
        SearchAbove = binding.SearchAbove is { } above ? (float)(above * pose.Scale) : null,
    };
    /// <summary>The implicit-owner pose normalization shared by expansion and unpack. An enumerated support or light child
    /// takes its owner placement's pose, the pose explicit records anchored on it declare.</summary>
    internal static MapPrefabChild OwnerPose(MapPrefabChild child, IReadOnlyList<MapPrefabChild> children,
        IReadOnlyDictionary<MapPrefabChildKey, MapPrefabChildKey> owners) =>
        child with { LocalTransform = MapPrefabMaterializationOverrides.Pose(children.OfType<MapPrefabPlacementChild>().Single(p => p.Key == owners[child.Key]).Value) };
}
