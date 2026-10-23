using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Selected decoded inputs share the same inventory registry as other borrowed preparation products.</summary>
internal sealed class MapPreparedInputBudget(MapPreparationInventory inventory) : IDisposable
{
    internal static MapPreparedInputBudget? Acquire(IEnumerable<MapPreparedInputLease> inputs, MapPreparationReservation? workspace = null) =>
        MapSceneAdmission.Current is { } admission
            ? new(MapPreparationInventory.Acquire(admission, inputs.SelectMany(i => i.BudgetInventory), workspace)) : null;
    internal MapPreparedInputBudget Retain() => new(inventory.Retain());
    internal void Release() => Dispose();
    public void Dispose() => inventory.Dispose();
}
