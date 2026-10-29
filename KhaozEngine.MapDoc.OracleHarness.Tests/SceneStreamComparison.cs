using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc.Render3D;
using static KhaozEngine.Tests.MapDocOracle.ShippedSceneComparison;

namespace KhaozEngine.Tests.MapDocOracle;

internal sealed record SceneStreamResource(MapRenderKey Key, string Payload, string Bounds, bool RigidFoliage);

/// <summary>Window envelopes remain exact while rigid foliage aliases share their verified model payload.</summary>
[UnsupportedOSPlatform("windows")]
internal static class SceneStreamComparison
{
    internal static SceneStreamResource Capture(MapPreparedResource resource) => new(resource.Reference.Key,
        PayloadFingerprint(resource), System.Text.Json.JsonSerializer.Serialize(resource.Bounds, ShippedScenePolicy.Json),
        resource.Reference.Key.Kind == MapRenderKind.Foliage && resource.Payload is MapRigidResource);

    internal static void Window(IReadOnlyDictionary<MapRenderKey, SceneStreamResource> baseline,
        IReadOnlyList<MapPreparedResource> actual, PrivateOracleReport report)
    {
        var rows = actual.GroupBy(r => r.Reference.Key).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var key in baseline.Keys.Union(rows.Keys))
        {
            bool one = baseline.ContainsKey(key) && rows.TryGetValue(key, out var found) && found.Length == 1;
            CheckResource(report, "scene-stream-union", one, new { key, differingField = "window-resource-count" }, key, "window-resource-count");
            if (!one) continue;
            var expected = baseline[key]; var observed = Capture(rows[key][0]);
            CheckResource(report, "scene-stream-union", expected.Payload == observed.Payload, new { key, differingField = "window-payload" }, key, "window-payload");
            CheckResource(report, "scene-stream-union", expected.Bounds == observed.Bounds, new { key, differingField = "window-bounds" }, key, "window-bounds");
        }
    }

    internal static void Union(IEnumerable<SceneStreamResource> baseline, IReadOnlyList<MapPreparedResource> actual, PrivateOracleReport report)
    {
        var expected = baseline.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
        var observed = actual.Select(Capture).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var key in expected.Keys.Union(observed.Keys))
        {
            bool present = expected.ContainsKey(key) && observed.ContainsKey(key);
            CheckResource(report, "scene-stream-union", present, new { key, differingField = "union-coverage" }, key, "union-coverage");
            if (!present) continue;
            foreach (var row in observed[key]) Compare(expected[key], row, report);
        }
    }

    static void Compare(SceneStreamResource expected, SceneStreamResource actual, PrivateOracleReport report)
    {
        var key = expected.Key;
        CheckResource(report, "scene-stream-union", expected.Payload == actual.Payload, new { key, differingField = "payload" }, key, "payload");
        CheckResource(report, "scene-stream-union", expected.RigidFoliage && actual.RigidFoliage || expected.Bounds == actual.Bounds,
            new { key, differingField = "bounds" }, key, "bounds");
    }
}
