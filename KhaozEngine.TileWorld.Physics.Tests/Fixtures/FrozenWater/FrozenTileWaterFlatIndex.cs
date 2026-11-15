using System;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// Frozen from 1599ccd75 for #1756. Do not synchronize with production optimisations.

internal sealed class FrozenTileWaterFlatIndex
{
    internal FrozenTileWaterBoxIndex Columns { get; }
    internal FrozenTileWaterBoxIndex Boundaries { get; }
    internal FrozenTileWaterBoxIndex Exterior { get; }
    readonly string? _world;
    readonly bool _sameWorld = true;

    internal FrozenTileWaterFlatIndex(ReadOnlySpan<TileWaterColumn> columns,
        ReadOnlySpan<TileWaterBoundary> boundaries, ReadOnlySpan<TileWaterBoundary> exterior)
    {
        // No source array or builder delegate is retained by a bounds tree.
        var capturedColumns = columns.ToArray();
        var capturedBoundaries = boundaries.ToArray();
        var capturedExterior = exterior.ToArray();
        _world = columns.IsEmpty ? null : columns[0].Domain.WorldId;
        foreach (var column in columns) _sameWorld &= column.Domain.WorldId == _world;
        Columns = new(columns.Length, i => (capturedColumns[i].Minimum, capturedColumns[i].Maximum));
        Boundaries = new(boundaries.Length, i => (capturedBoundaries[i].Minimum, capturedBoundaries[i].Maximum));
        Exterior = new(exterior.Length, i => (capturedExterior[i].Minimum, capturedExterior[i].Maximum));
    }

    internal bool MatchesWorld(string world) => Columns.Count == 0 || _sameWorld && _world == world;
}
