using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Resolves copied placement inputs, releasing their workspace before later product stages.</summary>
internal static class MapEffectivePlacementPreparation
{
    internal static IReadOnlyList<MapResolvedPlacement> Resolve(IReadOnlyList<MapPrefabChild> children, MapAssetClosure assets,
        IReadOnlyDictionary<string, MapEffectiveTransform> transforms, IMapSurfaceSource source, MapQueryLimits limits,
        IReadOnlySet<string> standalone, IReadOnlyDictionary<string, string> owners,
        out IReadOnlyList<MapPlacementSupport> supports, out IReadOnlyDictionary<string, MapSupportBinding> requests,
        out MapPreparationReservation? ownership)
    {
        using var preparation = MapSceneAdmission.Current?.BeginPreparation();
        MapSceneAdmission.Current?.ReserveBytes(checked(children.OfType<MapPrefabPlacementChild>()
            .Sum(c => 512L + 16L * c.Value.Tags.Count)));
        var inputs = new List<MapPlacement>();
        var supportRequests = new Dictionary<string, MapSupportBinding>(System.StringComparer.Ordinal);
        foreach (var child in children.OfType<MapPrefabPlacementChild>())
        {
            var pose = transforms[MapEffectiveContext.Key(MapPrefabChildKind.Placement, child.Value.Id)];
            var p = MapPrefabReferenceWalker.Copy(child.Value, child.Value.Id, child.Value.SupportBinding);
            p.X = (float)pose.X; p.Z = (float)pose.Z; p.Yaw = (float)pose.YawRadians; p.Scale = (float)pose.Scale;
            p.Y = child.Value.Y is null ? null : (float)pose.Y;
            if (p.SupportBinding is { } binding && !standalone.Contains(child.Value.Id))
            {
                p.SupportBinding = MapPrefabResolver.WorldRequest(binding, pose);
                supportRequests.Add(child.Value.Id, p.SupportBinding with { });
            }
            inputs.Add(p);
        }
        var result = MapResolverV2.ResolveEffectivePlacements(assets, inputs, transforms, source, limits, out supports, owners);
        requests = supportRequests;
        ownership = preparation?.Complete(checked(128L * result.Count + result.Sum(p => 8L * p.Tags.Count) +
            96L * (supports.Count + supportRequests.Count)));
        return result;
    }
}
