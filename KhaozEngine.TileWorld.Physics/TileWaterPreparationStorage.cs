using System;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;
using R = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Range;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>Optional hit preparations in one 4 MiB slab, plus one 176-basis, 10-plane workspace.</summary>
internal sealed class TileWaterPreparationStorage
{
    internal const int ByteBudget = 4 * 1024 * 1024;
    internal readonly TileWaterPreparationWorkspace Workspace = new();
    readonly byte[] _slab = new byte[ByteBudget];
    int _used, _retained, _misses, _exhausted, _fallbacks;
    internal readonly record struct Snapshot(int BudgetBytes, int UsedBytes, int WorkspaceBytes,
        int RetainedHits, int DiscardedMisses, int ExhaustedHits, int FallbackEvaluations);
    internal Snapshot Last { get; private set; }

    internal void BeginTrace() => _used = _retained = _misses = _exhausted = _fallbacks = 0;

    internal void EndTrace()
    {
        Last = new(ByteBudget, _used, Workspace.PayloadBytes, _retained, _misses, _exhausted, _fallbacks);
        // Every offset belongs to this trace. The bytes contain no references and are not observable afterward.
        _used = 0;
    }

    internal void DiscardMiss()
    {
        if (TileWaterQueryWork.Enabled) _misses++;
    }

    internal int Retain(ref TileWaterPreparedQuery prepared)
    {
        if (!prepared.CanRetain) return -1;
        int bytes = prepared.StorageBytes;
        if (bytes > ByteBudget - _used)
        {
            if (TileWaterQueryWork.Enabled) _exhausted++;
            return -1;
        }
        int offset = _used;
        prepared.CopyTo(_slab.AsSpan(offset, bytes));
        _used += bytes;
        if (TileWaterQueryWork.Enabled) _retained++;
        return offset;
    }

    internal R Evaluate(int offset, TileWaterConvexShape shape, V start, V drive, I core, float radius,
        double enter, double exit, double heightError, double horizontalError)
    {
        if (offset < 0)
        {
            if (TileWaterQueryWork.Enabled) _fallbacks++;
            return shape.Intersect(start, drive, core, radius, enter, exit, heightError, horizontalError);
        }
        var prepared = TileWaterPreparedQuery.Restore(shape, _slab.AsSpan(offset));
        return prepared.Evaluate(enter, exit);
    }
}
