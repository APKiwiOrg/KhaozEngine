using System;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Ecs;

/// <summary>Type-erased component storage for one component type within one archetype.</summary>
internal abstract class Column
{
    public abstract void EnsureCapacity(int capacity);
    public abstract void CopyRow(Column dest, int srcRow, int destRow);
    public abstract void SwapRemove(int row, int last);
    public abstract void ClearRow(int row);
    public abstract object GetBoxed(int row);
    public abstract void SetBoxed(int row, object value);
}

/// <summary>Contiguous storage for component type <typeparamref name="T"/> (a column in an archetype).</summary>
/// <remarks>
/// The backing array is a grow-only arena: <see cref="EnsureCapacity"/> doubles it and nothing ever shrinks it, so
/// an archetype that peaks at N rows keeps room for N for as long as it exists. That is deliberate. Archetype
/// population is a sawtooth (a wave of projectiles spawns, despawns, and the next wave spawns into the same rows),
/// and a shrink heuristic would hand the array back only to re-allocate and re-copy it moments later. What the
/// retained capacity must NOT do is retain component DATA, which is what <see cref="ClearRow"/> is for: past
/// <see cref="Archetype.Count"/> every slot holds <c>default</c>, so the arena costs zeroed bytes and pins nothing.
/// </remarks>
internal sealed class Column<T> : Column where T : struct
{
    public T[] Data = new T[8];

    public ref T Get(int row) => ref Data[row];
    public void Set(int row, T value) => Data[row] = value;

    public override void EnsureCapacity(int capacity)
    {
        if (Data.Length >= capacity) return;
        int n = Data.Length;
        while (n < capacity) n *= 2;
        Array.Resize(ref Data, n);
    }

    public override void CopyRow(Column dest, int srcRow, int destRow)
    {
        var d = (Column<T>)dest;
        d.EnsureCapacity(destRow + 1);
        d.Data[destRow] = Data[srcRow];
    }

    /// <summary>Moves row <paramref name="last"/> down into <paramref name="row"/> and clears the slot it came
    /// from. Callers pass distinct rows (<see cref="Archetype.SwapRemove"/> routes the <c>row == last</c> tail case
    /// to <see cref="ClearRow"/> instead), because copying a row onto itself and then clearing it would erase the
    /// surviving component.</summary>
    public override void SwapRemove(int row, int last)
    {
        Data[row] = Data[last];
        ClearRow(last);
    }

    /// <summary>Clears a vacated slot, so a component that carries a managed reference stops holding it once the
    /// row is dead (#119). Rows past <see cref="Archetype.Count"/> are never read, but they stay REACHABLE through
    /// <see cref="Data"/>, which is enough to keep whatever they point at alive for the life of the archetype.</summary>
    public override void ClearRow(int row)
    {
        // A JIT-time constant, so for an unmanaged T the whole body folds away and the despawn path pays nothing.
        // Only a T that actually carries a reference does the store, and for it the store IS the fix.
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Data[row] = default;
    }

    public override object GetBoxed(int row) => Data[row];

    public override void SetBoxed(int row, object value)
    {
        EnsureCapacity(row + 1);
        Data[row] = (T)value;
    }
}
