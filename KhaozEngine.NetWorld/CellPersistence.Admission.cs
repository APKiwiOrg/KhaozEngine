using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

public sealed partial class CellPersistence
{
    sealed record DeferredRestore(byte[] Body, byte[] Original, bool Migrated, int FromVersion, string? Detail);
    readonly Dictionary<CellCoord, DeferredRestore> deferredRestores = new();

    readonly HashSet<CellCoord> restoreHolds = new();

    internal IDisposable HoldRestore(CellCoord coord)
    {
        if (!restoreHolds.Add(coord)) throw new InvalidOperationException("This cell already has a cached restore owner.");
        return new RestoreHold(this, coord);
    }

    sealed class RestoreHold(CellPersistence owner, CellCoord coord) : IDisposable
    {
        bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            owner.restoreHolds.Remove(coord);
        }
    }

    void RetryDeferredRestores()
    {
        // One attempt per pending cell per drain. Keep the already-migrated body, original bytes
        // and load fence together so environmental readiness cannot quarantine or overwrite a save.
        foreach (var pair in deferredRestores.ToArray())
        {
            DeferredRestore pending = pair.Value;
            TryRestoreAndBaseline(pair.Key, pending.Body, pending.Original, pending.Migrated, pending.FromVersion, pending.Detail);
            if (!deferredRestores.ContainsKey(pair.Key)) loadsInFlight.TryRemove(pair.Key, out _);
        }
    }
}
