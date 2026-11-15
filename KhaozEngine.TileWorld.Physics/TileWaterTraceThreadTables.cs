using System;
using System.Threading;

namespace KhaozEngine.TileWorld.Physics;

// Sampler-owned tables have permanent thread claims. No thread-static root retains a discarded world.
internal abstract class TileWaterTraceThreadTables<T> where T : TileWaterTraceStorage
{
    internal const int ThreadLimit = 4;
    readonly Slot[] _slots = [new(), new(), new(), new()];
    sealed class Slot
    {
        internal int ThreadId;
        internal T? Table;
    }
    protected abstract T CreateTable();

    internal T? ForCurrentThread()
    {
        int thread = Environment.CurrentManagedThreadId;
        foreach (var slot in _slots)
        {
            int owner = Volatile.Read(ref slot.ThreadId);
            if (owner == thread) return slot.Table;
            if (owner == 0 && Interlocked.CompareExchange(ref slot.ThreadId, thread, 0) == 0)
                return slot.Table = CreateTable();
        }
        return null;
    }

    internal void ResetCurrentThread()
    {
        foreach (var slot in _slots)
            if (Volatile.Read(ref slot.ThreadId) == Environment.CurrentManagedThreadId)
                slot.Table?.Clear();
    }

    // A weak lifetime token. Its target is borrowed, never mutated by a test.
    internal WeakReference? CaptureCurrentTableForTests()
    {
        foreach (var slot in _slots)
            if (Volatile.Read(ref slot.ThreadId) == Environment.CurrentManagedThreadId && slot.Table is { } table)
                return new(table);
        return null;
    }
}
