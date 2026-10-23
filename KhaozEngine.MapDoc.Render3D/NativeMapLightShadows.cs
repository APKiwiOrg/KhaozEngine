using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Resolves declared emitter clearances in the submission anchor's space.</summary>
internal static class NativeMapLightShadows
{
    internal static LightShadow Resolve(MapScopedScene scene, MapEffectiveLight light, MapSubmissionAnchor anchor, long key)
    {
        if (!light.CastShadows) return LightShadow.None;
        var shadow = LightShadow.Static(key);
        var exclusion = light.ShadowExclusion;
        exclusion?.Validate(light.OwnerPlacementId is not null);
        if (exclusion?.Kind == MapLightShadowExclusionKind.None) return shadow;
        if (exclusion?.Kind == MapLightShadowExclusionKind.NearSphere) return LightShadow.Static(key, exclusion.Radius!.Value);
        if (light.OwnerPlacementId is not { } owner) return shadow;
        var placement = scene.Placements.SingleOrDefault(p => p.PlacementId == owner)
            ?? throw new MapDocumentException("Light fixture owner is absent from its scoped closure.");
        Vector3 low, high;
        float units = 1, margin = 0;
        if (exclusion is { Kind: MapLightShadowExclusionKind.FixtureBox })
        {
            low = exclusion.Min!.Value; high = exclusion.Max!.Value; margin = exclusion.Margin!.Value;
        }
        else
        {
            var asset = scene.Source.Data.Assets.GetAsset(placement.AssetId);
            var bounds = asset.RenderBounds;
            if (asset.LodBounds is { } lod) bounds = new(Vector3.Min(bounds.Min, lod.Min), Vector3.Max(bounds.Max, lod.Max));
            low = bounds.Min; high = bounds.Max; units = asset.SourceUnitsToMetres;
        }
        var matrix = MapRenderTransform.ToLocal(NativeMapPlacements.Pose(scene, placement), anchor);
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        for (int corner = 0; corner < 8; corner++)
        {
            var point = Vector3.Transform(new Vector3((corner & 1) == 0 ? low.X : high.X,
                (corner & 2) == 0 ? low.Y : high.Y, (corner & 4) == 0 ? low.Z : high.Z) * units, matrix);
            min = Vector3.Min(min, point); max = Vector3.Max(max, point);
        }
        if (exclusion is not null)
        {
            min -= new Vector3(margin); max += new Vector3(margin);
            if (!Finite(min) || !Finite(max) || min.X >= max.X || min.Y >= max.Y || min.Z >= max.Z)
                throw new MapDocumentException("Light fixture shadow exclusion is not a finite world-space box.");
        }
        return shadow.WithExclusionBox(min, max);
    }
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
