using System.Text.Json;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using static KhaozEngine.Tests.MapDocOracle.ShippedFoliageManifest;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Complete caller policy, covered by the provenance byte digest with the scene comparison.</summary>
internal sealed record SceneLimitsProfile(MapSceneLimits Scene, MapRenderBudgets Residency)
{
    internal static SceneLimitsProfile Read(JsonElement json)
    {
        Shape(json, "scene", "residency");
        var scene = json.GetProperty("scene");
        Shape(scene, "maxTiles", "maxMetadataRows", "maxMemberships", "maxMembershipsPerResource", "maxScannedBytes",
            "maxMetadataBytes", "maxSelectedResources", "maxPatchScopes", "maxPreparedFaces", "maxPreparedBytes");
        var residency = json.GetProperty("residency");
        Shape(residency, "maxWorkers", "maxQueuedPlans", "maxQueuedBytes", "maxAppliesPerUpdate", "maxUploadBytesPerUpdate",
            "maxDisposalsPerUpdate", "maxResidentCpuBytes", "maxResidentGpuBytes", "maxPlanBytes", "maxActiveWindows");
        return new(new(Int(scene, "maxTiles"), Int(scene, "maxMetadataRows"), Int(scene, "maxMemberships"),
            Int(scene, "maxMembershipsPerResource"), Long(scene, "maxScannedBytes"), Long(scene, "maxMetadataBytes"),
            Int(scene, "maxSelectedResources"), Int(scene, "maxPatchScopes"), Int(scene, "maxPreparedFaces"), Long(scene, "maxPreparedBytes")),
            new(Int(residency, "maxWorkers"), Int(residency, "maxQueuedPlans"), Long(residency, "maxQueuedBytes"),
            Int(residency, "maxAppliesPerUpdate"), Long(residency, "maxUploadBytesPerUpdate"), Int(residency, "maxDisposalsPerUpdate"),
            Long(residency, "maxResidentCpuBytes"), Long(residency, "maxResidentGpuBytes"), Long(residency, "maxPlanBytes"), Int(residency, "maxActiveWindows")));
    }

    static int Int(JsonElement json, string name) => (int)Long(json, name, int.MaxValue);
    static long Long(JsonElement json, string name, long max = long.MaxValue)
    {
        var value = json.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && number > 0 && number <= max,
            "invalid scene limits value " + name);
        return value.GetInt64();
    }
}
