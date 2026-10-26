using System.Collections.Generic;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Physical inventory retained by a composite, excluding separately owned foliage contexts and samples.</summary>
internal static class MapRenderPreparationInventory
{
    internal static long Bytes(IEnumerable<MapRenderPlan> plans)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        long bytes = 0;
        foreach (var plan in plans)
        {
            foreach (var cell in plan.Inventory.Water.Cells)
                if (seen.Add(cell)) bytes = checked(bytes + MapScopedWaterResult.CellInventoryBytes(cell));
            bytes = checked(bytes + plan.PreparationOwnership!.RootInventoryBytes(seen) +
                plan.Scene.PreparationOwnership!.RootInventoryBytes(seen));
        }
        return bytes;
    }
}
