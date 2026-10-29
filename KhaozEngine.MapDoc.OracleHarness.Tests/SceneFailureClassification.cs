using System;
using KhaozEngine.MapDoc.Render3D;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Resource identity and failed field, independent of retained discrepancy examples.</summary>
internal readonly record struct SceneFailureClassification(string ResourceKind, string KeyClass, string DifferingField)
{
    internal static SceneFailureClassification Resource(MapRenderKey key, string field) =>
        new(key.Kind.ToString(), Class(key.Kind.ToString(), key.PartId), field);

    internal static string Class(string kind, string? part) => part switch
    {
        "hlod" => "proxy",
        "instances" => "instances",
        { } id when kind == "Foliage" && id.StartsWith("model-part/", StringComparison.Ordinal) => "rigid-model-part",
        { } id when id.StartsWith("part/", StringComparison.Ordinal) => kind == "Foliage" ? "rigid-model-part" : "part",
        _ => kind switch
        {
            "Terrain" => "partition",
            "Water" => "cell",
            "Light" => "light",
            "Placement" or "Roof" => "placement",
            _ => "resource"
        }
    };
}
