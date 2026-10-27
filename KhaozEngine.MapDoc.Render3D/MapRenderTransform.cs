using System;
using System.Numerics;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Render3D;

public readonly record struct MapFrameState(WorldFrame Frame, MapFramePoint Observer, MapRoofMode RoofMode,
    double TimeSeconds, int WindSeed, MapFramePoint Focus, MapFoliageFrameState? FoliageState = null);

/// <summary>Reduces precise poses and focus before their single local binary32 rounding.</summary>
public static class MapRenderTransform
{
    internal static PropPlacement ToWeldPlacement(string assetId, MapEffectiveTransform pose) =>
        new(assetId, Single(pose.X), Single(pose.Y), Single(pose.Z), Single(pose.Scale), Single(pose.YawRadians), 0);

    public static Matrix4x4 ToLocal(MapEffectiveTransform pose, MapSubmissionAnchor anchor)
    {
        pose = MapEffectiveTransform.Valid(pose);
        float scale = Single(pose.Scale);
        if (scale == 0) throw new MapDocumentException("Render scale is not representable.");
        var (s, c) = Math.SinCos(pose.YawRadians);
        return new(Single(c * pose.Scale), 0, Single(-s * pose.Scale), 0,
            0, scale, 0, 0, Single(s * pose.Scale), 0, Single(c * pose.Scale), 0,
            Single(pose.X - anchor.X), Single(pose.Y - anchor.Y), Single(pose.Z - anchor.Z), 1);
    }

    static float Single(double value)
    {
        float result = (float)value;
        return float.IsFinite(result) ? result : throw new MapDocumentException("Render transform is not representable.");
    }

    public static Vector3 FocusLocal(MapFramePoint focus, MapSubmissionAnchor anchor)
    {
        var xz = focus.ExactWorldXz();
        return MapSubmissionGeometry.Offset(new(xz.X, focus.ExactY(), xz.Z), anchor);
    }
}
