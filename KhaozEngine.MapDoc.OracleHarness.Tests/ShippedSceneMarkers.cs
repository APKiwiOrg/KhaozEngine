using System.Runtime.Versioning;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using static KhaozEngine.Tests.MapDocOracle.ShippedSceneComparison;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
internal static class ShippedSceneMarkers
{
    internal static void Head(ShippedSceneInputs inputs, MapDocument document, MapAssetClosure closure, PrivateOracleReport report)
    {
        if (inputs.Markers is null && (document.BuildingIntent?.Markers.Count ?? 0) == 0 && (document.Prefabs?.Instances.Count ?? 0) == 0) return;
        var effective = inputs.Manifest.Limits is { HasQueryProfile: true } limits
            ? MapPrefabResolver.ExpandWithLimits(document, closure, limits.Queries) : MapPrefabResolver.Expand(document, closure);
        if (inputs.Markers is not { } source)
        {
            Check(report, "scene-marker", MapEffectiveAnnotations.Markers(effective).Count == 0, new { head = true });
            return;
        }
        var result = ShippedMarkerComparison.CompareScene(source, MapEffectiveAnnotations.Markers(effective), effective.CompleteView());
        report.Check("scene-marker", result.Failures.Count == 0, result.DetailJson);
    }

    internal static void Plan(ShippedSceneInputs inputs, MapRenderPlan plan, PrivateOracleReport report)
    {
        if (inputs.Markers is not { } source)
        {
            Check(report, "scene-marker", plan.Inventory.Markers.Count == 0, new { plan = true });
            return;
        }
        var result = ShippedMarkerComparison.CompareScene(source, plan.Inventory.Markers, plan.Scene.SurfaceView, plan.Scene.Scope);
        report.Check("scene-marker", result.Failures.Count == 0, result.DetailJson);
    }
}
