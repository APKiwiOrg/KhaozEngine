using System;

namespace KhaozEngine.Ecs;

/// <summary>Type-erased component storage for one component type within one archetype.</summary>
internal abstract class Column
{
    public abstract void EnsureCapacity(int capacity);
    public abstract void CopyRow(Column dest, int srcRow, int destRow);
    public abstract void SwapRemove(int row, int last);
    public abstract object GetBoxed(int row);
    public abstract void SetBoxed(int row, object value);
}

/// <summary>Contiguous storage for component type <typeparamref name="T"/> (a column in an archetype).</summary>
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

    public override void SwapRemove(int row, int last) => Data[row] = Data[last];

    public override object GetBoxed(int row) => Data[row];

    public override void SetBoxed(int row, object value)
    {
        EnsureCapacity(row + 1);
        Data[row] = (T)value;
    }
}
