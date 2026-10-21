using System;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
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
        var buffer = new MapRoofEvaluationBuffer(resources.BuildingIntent, view);
        buffer.Evaluate(observer, mode);
        return buffer.Detach();
    }

    public static MapRoofIntentResult Evaluate(MapScopedScene scene, MapFramePoint observer, MapRoofMode mode)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var buffer = new MapRoofEvaluationBuffer(scene);
        buffer.Evaluate(observer, mode);
        return buffer.Detach();
    }
}
