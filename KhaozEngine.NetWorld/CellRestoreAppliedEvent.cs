using System.Collections.Generic;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Describes one cell snapshot that <see cref="CellPersistence"/> successfully applied. <see cref="Coord"/> names
/// the live cell, <see cref="NetIds"/> contains the entities the host restored, and
/// <see cref="RetainedFrameCount"/> reports unknown extension frames preserved by the restore.
/// </summary>
public readonly record struct CellRestoreAppliedEvent(
    CellCoord Coord,
    IReadOnlyList<long> NetIds,
    int RetainedFrameCount);
