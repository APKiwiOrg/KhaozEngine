using System;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Interiors;

/// <summary>Evaluates explicit concealment links against acquired room membership. Groups expand only their
/// attached link, without portal traversal, ancestor matching or changes to statics.</summary>
public static class MapRoofPolicy
{
    public static MapRoofIntentResult Evaluate(MapEffectiveResources resources, MapScopedSurfaces view,
        MapFramePoint observer, MapRoofMode mode)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(view);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        resources.RequireView(view);
        var membership = new MapSpaceMembership(view).Query(observer);
        var intent = new MapRoofIntentRows(resources.BuildingIntent);
        intent.Evaluate(new(membership.Status, membership.SpaceId, membership.ExactPoint, membership.Witness), mode);
        return new(membership.Status, membership.SpaceId, intent.Detach(), membership.Witness);
    }

    public static MapRoofIntentResult Evaluate(MapScopedScene scene, MapFramePoint observer, MapRoofMode mode)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var prepared = MapRoofEvaluationBuffer.Prepare(scene);
        if (prepared.Buffer is not { } buffer)
            return new(prepared.Status == MapSceneStatus.CapacityExceeded ? MapMembershipStatus.CapacityExceeded :
                prepared.Status == MapSceneStatus.NotRepresentable ? MapMembershipStatus.NotRepresentable : MapMembershipStatus.Invalid,
                null, Array.Empty<MapRoofIntent>(), scene.SurfaceView.ReadWitness);
        buffer.Evaluate(observer, mode);
        return buffer.Detach();
    }
}
