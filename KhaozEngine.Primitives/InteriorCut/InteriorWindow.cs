using System;
using System.Numerics;

namespace KhaozEngine.Primitives;

/// <summary>A square world-XZ window with rows along positive Z.</summary>
public readonly record struct InteriorWindow(Vector2 Min, float CellSize, int Cells)
{
    public const int MaxCells = 4096;

    /// <summary>Round the requested extent up to cells, capped by the guaranteed texture dimension.</summary>
    public static int CellsFor(float extentMetres, float cellSize)
    {
        RequirePositive(extentMetres, nameof(extentMetres));
        RequirePositive(cellSize, nameof(cellSize));
        return (int)Math.Min(MaxCells, Math.Ceiling((double)extentMetres / cellSize));
    }

    /// <summary>Centre on the focus, snapping the minimum down to the world-origin cell lattice.</summary>
    public static InteriorWindow Centred(Vector2 focus, float cellSize, int cells)
    {
        RequireFinite(focus, nameof(focus));
        RequirePositive(cellSize, nameof(cellSize));
        double half = (double)cellSize * cells / 2d;
        var min = new Vector2(
            InteriorLattice.Edge(Math.Floor(((double)focus.X - half) / cellSize), cellSize),
            InteriorLattice.Edge(Math.Floor(((double)focus.Y - half) / cellSize), cellSize));
        var window = new InteriorWindow(min, cellSize, cells);
        window.Validate();
        return window;
    }

    public bool NeedsRecentre(Vector2 focus, float distance)
    {
        Validate();
        RequireFinite(focus, nameof(focus));
        RequirePositive(distance, nameof(distance));
        double half = (double)CellSize * Cells / 2d;
        return Math.Abs(focus.X - ((double)Min.X + half)) >= distance ||
            Math.Abs(focus.Y - ((double)Min.Y + half)) >= distance;
    }

    internal void Validate()
    {
        RequireFinite(Min, nameof(Min));
        RequirePositive(CellSize, nameof(CellSize));
        if (Cells < 1 || Cells > MaxCells)
            throw new ArgumentOutOfRangeException(nameof(Cells), "Cell count must lie in 1..4096.");
        float extent = CellSize * Cells;
        Vector2 max = Min + new Vector2(extent);
        if (!float.IsFinite(extent) || !float.IsFinite(max.X) || !float.IsFinite(max.Y) || max.X <= Min.X || max.Y <= Min.Y)
            throw new ArgumentException("Window extent must be finite and representable.");
    }

    static void RequirePositive(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0f)
            throw new ArgumentOutOfRangeException(name, "Value must be finite and positive.");
    }

    static void RequireFinite(Vector2 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            throw new ArgumentException("Coordinates must be finite.", name);
    }
}
