using System;
using System.Numerics;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Surfaces;
using static KhaozEngine.Tests.MapDocOracle.ShippedSceneComparison;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Compares spatial representation in a common frame while retaining exact semantic values.</summary>
[UnsupportedOSPlatform("windows")]
internal static class SceneSubmissionGeometry
{
    internal static bool IsProxy(MapRenderKey key) => key.Kind is MapRenderKind.Placement or MapRenderKind.Roof && key.PartId == "hlod";
    static bool AnchoredGeometry(MapDrawRecord draw) => draw.Rigid is not null || draw.Resource.Key.Kind == MapRenderKind.Light;

    internal static bool PoseEqual(MapDrawRecord a, MapDrawRecord b)
    {
        if (!AnchoredGeometry(a)) return a.Anchor == b.Anchor && MatrixNear(a.LocalTransform, b.LocalTransform);
        if (!PointEqual(a.LocalTransform.Translation, a.Anchor, b.LocalTransform.Translation, b.Anchor)) return false;
        var x = a.LocalTransform; var y = b.LocalTransform;
        x.Translation = Vector3.Zero; y.Translation = Vector3.Zero;
        return MatrixNear(x, y);
    }

    internal static bool FocusEqual(MapDrawRecord a, MapDrawRecord b) => AnchoredGeometry(a)
        ? PointEqual(a.LocalFocus, a.Anchor, b.LocalFocus, b.Anchor) : Bits(a.LocalFocus, b.LocalFocus);

    internal static bool LightEqual(MapDrawRecord a, MapDrawRecord b)
    {
        if (a.Light is not { } x || b.Light is not { } y) return a.Light is null && b.Light is null;
        if (!Bits(x with { Shadow = default }, y with { Shadow = default }) || x.Shadow.HasExclusionBox != y.Shadow.HasExclusionBox) return false;
        if (!x.Shadow.HasExclusionBox) return Bits(x.Shadow, y.Shadow);
        return Bits(x.Shadow with { ExclusionMin = default, ExclusionMax = default }, y.Shadow with { ExclusionMin = default, ExclusionMax = default }) &&
            PointEqual(x.Shadow.ExclusionMin, a.Anchor, y.Shadow.ExclusionMin, b.Anchor) &&
            PointEqual(x.Shadow.ExclusionMax, a.Anchor, y.Shadow.ExclusionMax, b.Anchor);
    }

    internal static bool DeclaredLightEqual(MapEffectiveLight a, MapEffectiveLight b) =>
        Bits(a with { ShadowExclusion = null }, b with { ShadowExclusion = null }) && ExclusionEqual(a.ShadowExclusion, b.ShadowExclusion);

    internal static bool ExclusionEqual(MapLightShadowExclusion? a, MapLightShadowExclusion? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Kind != MapLightShadowExclusionKind.FixtureBox || b.Kind != MapLightShadowExclusionKind.FixtureBox) return Bits(a, b);
        // These declarations share the mapped owner's local metre frame. Clearances remain exact.
        return Bits(a with { Min = null, Max = null }, b with { Min = null, Max = null }) &&
            a.Min is { } minA && b.Min is { } minB && PointEqual(minA, default, minB, default) &&
            a.Max is { } maxA && b.Max is { } maxB && PointEqual(maxA, default, maxB, default);
    }

    internal static bool PointEqual(Vector3 a, MapSubmissionAnchor anchorA, Vector3 b, MapSubmissionAnchor anchorB) =>
        Axis(a.X, anchorA.X, b.X, anchorB.X) && Axis(a.Y, anchorA.Y, b.Y, anchorB.Y) && Axis(a.Z, anchorA.Z, b.Z, anchorB.Z);
    static bool Axis(float a, long anchorA, float b, long anchorB) => float.IsFinite(a) && float.IsFinite(b) &&
        Math.Abs(Offset(anchorA, anchorB) + (double)a - b) <= GeometryTolerance;
    // Subtract integer anchors before floating conversion, including beyond binary64's exact integer range.
    internal static double Offset(long a, long b) => (double)((decimal)a - b);
}
