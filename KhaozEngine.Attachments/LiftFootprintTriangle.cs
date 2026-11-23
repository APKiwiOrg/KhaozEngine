using System;

namespace KhaozEngine.Attachments;

/// <summary>A piece triangle's cells and, for fixed triangles, its per-cell heights.</summary>
/// <remarks>The default value describes a fixed triangle without entries.</remarks>
public readonly record struct LiftFootprintTriangle
{
    internal const int UnitsPerMetre = 100_000;
    internal const ushort MaxHeightUnits = 25_000;

    readonly Entries? entries;
    readonly int cellStart;
    readonly int heightStart;
    readonly int count;

    /// <summary>
    /// Admit an owned snapshot of strictly ascending cell keys and one measured height per key for a fixed triangle.
    /// A swinging triangle takes an empty height span.
    /// </summary>
    /// <exception cref="LiftFootprintDataException">A key or height violates the triangle's rules.</exception>
    public LiftFootprintTriangle(bool swinging, ReadOnlySpan<int> cellKeys, ReadOnlySpan<double> heightsMetres)
    {
        if (swinging && !heightsMetres.IsEmpty)
            throw new LiftFootprintDataException(LiftFootprintRefusal.SwingingTriangleHasHeights, "Swinging triangles carry cells only.")
            { EntryIndex = 0, HeightMetres = heightsMetres[0] };
        if (!swinging && heightsMetres.Length != cellKeys.Length)
            throw new LiftFootprintDataException(LiftFootprintRefusal.FixedTriangleHeightCountMismatch, "Fixed triangles require one height per cell.");

        int[] keys = cellKeys.ToArray();
        ushort[] units = new ushort[heightsMetres.Length];
        int previous = -1;
        for (int index = 0; index < keys.Length; index++)
        {
            int key = keys[index];
            if (key < 0)
                throw new LiftFootprintDataException(LiftFootprintRefusal.CellKeyNegative, "Cell keys must be nonnegative.")
                { EntryIndex = index, CellKey = key };
            if (key <= previous)
                throw new LiftFootprintDataException(LiftFootprintRefusal.CellKeysNotAscending, "Cell keys must increase strictly.")
                { EntryIndex = index, CellKey = key };
            previous = key;
            if (swinging)
                continue;

            double metres = heightsMetres[index];
            if (!double.IsFinite(metres) || metres < 0 || metres > ToMetres(MaxHeightUnits))
                throw new LiftFootprintDataException(LiftFootprintRefusal.HeightMetresOutsideRange, "A height must be finite and in [0, 0.25] metres.")
                { EntryIndex = index, CellKey = key, HeightMetres = metres };

            units[index] = RoundHeightUp(metres);
        }

        entries = keys.Length == 0 ? null : new Entries(keys, units);
        cellStart = 0;
        heightStart = swinging ? -1 : 0;
        count = keys.Length;
    }

    internal LiftFootprintTriangle(Entries entries, int cellStart, int heightStart, int count)
    {
        this.entries = entries;
        this.cellStart = cellStart;
        this.heightStart = heightStart;
        this.count = count;
    }

    /// <summary>Whether the triangle moves with a chain and carries only cells.</summary>
    public bool Swinging => heightStart < 0;

    /// <summary>The triangle's cell keys in strictly ascending order.</summary>
    public ReadOnlySpan<int> CellKeys => count == 0 ? [] : entries!.CellKeys.AsSpan(cellStart, count);

    /// <summary>
    /// The heights for <see cref="CellKeys"/> in 0.01 mm units, empty for a swinging triangle.
    /// Measured heights round up to the least unit whose height is at least the supplied value.
    /// </summary>
    public ReadOnlySpan<ushort> HeightUnits => Swinging || count == 0 ? [] : entries!.HeightUnits.AsSpan(heightStart, count);

    /// <summary>The rounded height in metres of the specified entry of a fixed triangle.</summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no height at the entry index.</exception>
    public double HeightMetres(int entryIndex)
    {
        ReadOnlySpan<ushort> units = HeightUnits;
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entryIndex, units.Length);
        return ToMetres(units[entryIndex]);
    }

    internal static double ToMetres(int units) => units / (double)UnitsPerMetre;

    internal static ushort RoundHeightUp(double metres)
    {
        // Compare with the decoded value to keep already-quantized heights on their original unit.
        int floor = (int)(metres * UnitsPerMetre);
        return (ushort)(ToMetres(floor) < metres ? floor + 1 : floor);
    }

    internal sealed class Entries(int[] cellKeys, ushort[] heightUnits)
    {
        internal int[] CellKeys { get; } = cellKeys;
        internal ushort[] HeightUnits { get; } = heightUnits;
    }
}
