using System;

namespace KhaozEngine.MapDoc.Prefabs;

internal static partial class MapPrefabPreflight
{
    internal static void CheckCounterForTests(MapPrefabExpansionLimits limits, string field, long count)
    {
        var counter = new Counter(limits, null);
        switch (field)
        {
            case "Instances": counter.Instances = count; break;
            case "Children": counter.Children = count; break;
            case "ReferenceEdges": counter.ReferenceEdges = count; break;
            case "Patches": counter.Patches = count; break;
            case "Records": counter.Records = count; break;
            case "GeometryElements": counter.GeometryElements = count; break;
            case "Bindings": counter.Bindings = count; break;
            case "Overrides": counter.Overrides = count; break;
            case "Transfers": counter.Transfers = count; break;
            case "PayloadBytes": counter.PayloadBytes = count; break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
        counter.Check();
    }
}
