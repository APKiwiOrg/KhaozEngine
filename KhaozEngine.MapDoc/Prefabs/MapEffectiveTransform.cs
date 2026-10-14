using System;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>A persisted binary64 mathematical pose, independent of rounded backend placement poses.</summary>
public readonly record struct MapEffectiveTransform(double X, double Y, double Z, double YawRadians, double Scale)
{
    public static MapEffectiveTransform From(MapTransform authored) =>
        Valid(new(authored.Position.X, authored.Position.Y, authored.Position.Z, authored.YawRadians, authored.Scale));

    public static MapEffectiveTransform Compose(MapEffectiveTransform parent, MapTransform local) => Compose(parent, From(local));

    public static MapEffectiveTransform Compose(MapEffectiveTransform parent, MapEffectiveTransform local)
    {
        Valid(parent);
        Valid(local);
        var (s, c) = Math.SinCos(parent.YawRadians);
        double x = local.X * parent.Scale, z = local.Z * parent.Scale;
        return Valid(new(parent.X + c * x + s * z, parent.Y + local.Y * parent.Scale,
            parent.Z - s * x + c * z, parent.YawRadians + local.YawRadians, parent.Scale * local.Scale));
    }

    internal static MapEffectiveTransform Valid(MapEffectiveTransform pose)
    {
        if (!double.IsFinite(pose.X) || !double.IsFinite(pose.Y) || !double.IsFinite(pose.Z) ||
            !double.IsFinite(pose.YawRadians) || !double.IsFinite(pose.Scale) || pose.Scale <= 0)
            throw new MapDocumentException("Effective pose must be finite with positive uniform scale.");
        return pose with { X = Zero(pose.X), Y = Zero(pose.Y), Z = Zero(pose.Z), YawRadians = Zero(pose.YawRadians) };
    }
    static double Zero(double value) => value == 0 ? 0 : value;
}

/// <summary>The precise pose retained by a standalone resource after unpack.</summary>
public sealed record MapStandaloneResourceTransform(MapPrefabChildKind Kind, string OwnerId,
    MapEffectiveTransform Transform, string SourceDigest);
