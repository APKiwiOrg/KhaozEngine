using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Render3D;
using static KhaozEngine.Tests.MapDocOracle.ShippedFoliageManifest;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Executes source declarations without calling native light preparation or using native owner bounds.</summary>
internal static class ShippedSceneLightShadows
{
    internal static MapLightShadowExclusion? Read(JsonElement row)
    {
        if (!row.TryGetProperty("shadowExclusion", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        string[] fields = value.GetProperty("kind").GetString() switch
        {
            "None" => ["kind"],
            "NearSphere" => ["kind", "radius"],
            "FixtureBox" => ["kind", "min", "max", "margin"],
            _ => throw new InvalidDataException("Unsupported source light shadow exclusion.")
        };
        Shape(value, fields);
        if (value.TryGetProperty("min", out var min)) Shape(min, "x", "y", "z");
        if (value.TryGetProperty("max", out var max)) Shape(max, "x", "y", "z");
        var result = value.Deserialize<MapLightShadowExclusion>(ShippedScenePolicy.Json)
            ?? throw new InvalidDataException("Missing source light shadow exclusion.");
        result.Validate(true);
        return result;
    }

    internal static LightShadow Resolve(SceneSourceLight light, LegacyScenePlacement owner, MapSubmissionAnchor anchor, long key)
    {
        if (!light.CastShadows) return LightShadow.None;
        var shadow = LightShadow.Static(key);
        var exclusion = light.ShadowExclusion;
        if (exclusion?.Kind == MapLightShadowExclusionKind.None) return shadow;
        if (exclusion?.Kind == MapLightShadowExclusionKind.NearSphere) return LightShadow.Static(key, exclusion.Radius!.Value);
        var matrix = owner.World; matrix.Translation -= new Vector3((float)anchor.X, (float)anchor.Y, (float)anchor.Z);
        if (exclusion is null)
        {
            var points = owner.Tiers.SelectMany(t => t).SelectMany(p => p.Mesh.Vertices).Select(v => Vector3.Transform(v.Position, matrix)).ToArray();
            return shadow.WithExclusionBox(points.Aggregate(Vector3.Min), points.Aggregate(Vector3.Max));
        }
        var min = exclusion.Min!.Value; var max = exclusion.Max!.Value;
        var corners = Enumerable.Range(0, 8).Select(corner => Vector3.Transform(new Vector3(
            (corner & 1) == 0 ? min.X : max.X, (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z), matrix)).ToArray();
        var padding = new Vector3(exclusion.Margin!.Value);
        return shadow.WithExclusionBox(corners.Aggregate(Vector3.Min) - padding, corners.Aggregate(Vector3.Max) + padding);
    }
}
