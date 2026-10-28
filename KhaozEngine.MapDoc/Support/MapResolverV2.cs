using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Support;

/// <summary>Resolves authored support bindings while preserving explicit placement heights and native snapshots.</summary>
public static class MapResolverV2
{
    public static MapSupportedResolution Resolve(MapDocument document, MapAssetClosure assets,
        IMapSurfaceSource surfaces, MapResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(surfaces);
        string hash = MapAuthoredIdentityV2.Compute(document, assets, options);
        var effective = MapPrefabResolver.Expand(document, assets, options.ExpansionLimits, null, surfaces, options.QueryLimits);
        var resolved = new MapResolvedDocument(effective.Placements, assets, MapResolvedBounds.Of(document.PlayableBounds!),
            MapResolvedBounds.Of(document.Bounds), hash, effective);
        return new(resolved, effective.PlacementSupports);
    }

    internal static IReadOnlyList<MapResolvedPlacement> ResolveEffectivePlacements(MapAssetClosure assets,
        IReadOnlyList<MapPlacement> effective, IReadOnlyDictionary<string, MapEffectiveTransform> transforms,
        IMapSurfaceSource surfaces, MapQueryLimits limits, out IReadOnlyList<MapPlacementSupport> supports, IReadOnlyDictionary<string, string>? owners = null)
    {
        var result = new List<MapResolvedPlacement>();
        var selected = new List<MapPlacementSupport>();
        var missingY = effective.Where(p => p.Y is null).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var placement in effective)
        {
            var pose = transforms[MapEffectiveContext.Key(MapPrefabChildKind.Placement, placement.Id)];
            MapResourceTransform.RequireWithinWorld(placement.Id, pose.X, pose.Y, pose.Z);
            _ = new MapResourceTransform(pose).Point(new(default, default, default));
            float presentationScale = (float)pose.Scale, presentationYaw = (float)pose.YawRadians;
            if (!float.IsFinite(presentationScale) || presentationScale <= 0 || !float.IsFinite(presentationYaw))
                throw new MapDocumentException($"Placement '{placement.Id}' backend pose is NotRepresentable.");
            float y = (float)pose.Y;
            if (placement.Y is null)
            {
                var binding = RequireBinding(placement);
                var support = SeatSupport(new MapResourceTransform(pose).Point(new(default, default, default)), binding, surfaces,
                    assets.Roots.Select(a => a.Sha256).ToArray(), limits);
                if (support.Status != MapSupportStatus.Supported) throw support.ResolutionRefusal($"Placement '{placement.Id}'");
                if (support.Face is { } face && owners is not null &&
                    owners.TryGetValue(MapEffectiveContext.Key(MapPrefabChildKind.Surface, face.OwnerId), out var provider) && missingY.Contains(provider))
                    throw new MapDocumentException($"Placement '{placement.Id}' with missing Y cannot bind support supplied by placement '{provider}' with missing Y.");
                y = support.WorldY;
                selected.Add(new(placement.Id, support));
            }
            result.Add(new(placement.Id, placement.Kind, placement.AssetId!, placement.NumericId,
                new(new((float)pose.X, y, (float)pose.Z), presentationYaw, presentationScale), placement.Tags));
        }
        supports = Array.AsReadOnly(selected.ToArray());
        return result.AsReadOnly();
    }

    /// <summary>The one support answer a world binding seats on at the exact XZ of <paramref name="point"/>, shared by
    /// support-bound placements and Support-height markers. The query point takes the binding's reference height. A
    /// Surface binding samples only its owner. A Space binding searches below and above that height by its two extents.
    /// The per-point scope spans 2 m around the point in its nearest frame, with every role and the given budgets. Callers
    /// own their refusal wording.</summary>
    internal static MapSupportResult SeatSupport(MapExactPoint point, MapSupportBinding binding, IMapSurfaceSource surfaces,
        IReadOnlyList<string> assetSha256, MapQueryLimits limits, Scene.MapAcquisitionCaller caller = Scene.MapAcquisitionCaller.Seat)
    {
        using var diagnostics = Scene.MapSceneAdmission.Current?.AcquisitionDiagnostics?.Enter(Scene.MapAcquisitionStage.Seat, caller);
        using var preparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
        Scene.MapSceneAdmission.Current?.ReserveBytes(96);
        var world = point with { Y = MapExactValue.FromSingle(binding.ReferenceY ?? 0) };
        var frame = WorldFrame.Nearest(world.X.ToSingle(), world.Z.ToSingle());
        float x = world.X.Subtract(new((long)frame.X * (long)WorldFrame.Grid, 1)).ToSingle();
        float z = world.Z.Subtract(new((long)frame.Z * (long)WorldFrame.Grid, 1)).ToSingle();
        var scope = new MapSurfaceScope(frame, new(x - 2, z - 2), new(x + 2, z + 2), null, null, Enum.GetValues<MapSurfaceRole>(), null, limits);
        var view = MapScopedSurfaces.Acquire(surfaces, scope, assetSha256, MapScopedSurfaces.EffectiveQueryPolicyVersion,
            MapScopedSurfaces.EffectiveBuildPolicyVersion);
        Scene.MapSceneAdmission.Current?.ReserveBytes(checked(256L + 64L * view.Surfaces.Count + 64L * view.Witness.Records.Count));
        var query = new MapSupportQuery(view);
        var result = binding.Kind == MapSupportBindingKind.Surface ? query.SampleSurfaceExact(world, binding.SurfaceId!)
            : query.SelectExact(new(MapEffectiveGeometry.Presentation(view, world), null, binding.SpaceId, null, binding.SearchAbove!.Value, binding.SearchBelow!.Value, null), world);
        preparation?.Complete(96, retainDependencies: false);
        return result;
    }

    /// <summary>Resolves <paramref name="placements"/>, which belong to <paramref name="document"/>, in the given order
    /// exactly as <see cref="Resolve"/> resolves them, without the authored hash and without resolving any other
    /// placement. The document's surfaces are captured only when a placement takes its height from support, and each
    /// support scope takes <paramref name="limits"/>.</summary>
    internal static IReadOnlyList<MapResolvedPlacement> ResolvePlacements(MapDocument document, MapAssetClosure assets,
        IReadOnlyList<MapPlacement> placements, MapQueryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        IMapSurfaceSource? surfaces = null;
        var resolved = new MapResolvedPlacement[placements.Count];
        for (int i = 0; i < resolved.Length; i++)
        {
            MapPlacement placement = placements[i];
            MapSupportBinding? binding = placement.Y is null ? RequireBinding(placement) : null;
            resolved[i] = MapResolver.ResolveOne(placement, (id, x, z) =>
                SupportHeight(id, binding!, x, z, surfaces ??= MapDocumentSurfaceSource.Capture(document), assets, limits, null));
        }
        return Array.AsReadOnly(resolved);
    }

    // A detached copy of the authored binding of a placement without an explicit height, after refusing an invalid one.
    static MapSupportBinding RequireBinding(MapPlacement placement)
    {
        MapSupportBinding binding = placement.SupportBinding ??
            throw new MapDocumentException($"Placement '{placement.Id}' support status Invalid: missing authored binding.");
        if (binding.Kind == MapSupportBindingKind.Space &&
            (binding.ReferenceY is null || binding.SearchBelow is null || binding.SearchAbove is null))
            throw new MapDocumentException($"Placement '{placement.Id}' support status Invalid: space binding requires reference Y and bounded search.");
        return binding with { };
    }

    static float SupportHeight(string id, MapSupportBinding binding, float x, float z, IMapSurfaceSource surfaces,
        MapAssetClosure assets, MapQueryLimits limits, List<MapPlacementSupport>? supports)
    {
        try { return SelectHeight(id, binding, x, z, surfaces, assets, limits, supports); }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        { throw new MapDocumentException($"Placement '{id}' support status NotRepresentable: overflow."); }
    }

    static float SelectHeight(string id, MapSupportBinding binding, float x, float z, IMapSurfaceSource surfaces,
        MapAssetClosure assets, MapQueryLimits limits, List<MapPlacementSupport>? supports)
    {
        WorldFrame frame = WorldFrame.Nearest(x, z);
        // Subtract integer anchors exactly before the one local float conversion.
        float localX = MapExactValue.FromSingle(x).Subtract(new((long)frame.X * (long)WorldFrame.Grid, 1)).ToSingle();
        float localZ = MapExactValue.FromSingle(z).Subtract(new((long)frame.Z * (long)WorldFrame.Grid, 1)).ToSingle();
        var point = new MapFramePoint(frame, new(localX, binding.ReferenceY ?? 0, localZ));
        var scope = new MapSurfaceScope(frame, new Vector2(localX - 2, localZ - 2), new Vector2(localX + 2, localZ + 2),
            null, null, Enum.GetValues<MapSurfaceRole>(), null, limits);
        MapScopedSurfaces scoped = MapScopedSurfaces.Acquire(surfaces, scope, assets.Roots.Select(a => a.Sha256).ToArray());
        var query = new MapSupportQuery(scoped);
        MapSupportResult support = binding.Kind == MapSupportBindingKind.Surface
            ? query.SampleSurface(point, binding.SurfaceId!)
            : query.Select(new(point, null, binding.SpaceId, null, binding.SearchAbove!.Value, binding.SearchBelow!.Value, null));
        if (support.Status != MapSupportStatus.Supported)
            throw support.ResolutionRefusal($"Placement '{id}'");
        supports?.Add(new(id, support));
        return support.WorldY;
    }
}
