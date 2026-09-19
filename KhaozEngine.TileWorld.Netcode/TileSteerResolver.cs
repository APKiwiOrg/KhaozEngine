namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// Turns one HELD direction into the step a body actually takes this boundary, with no search. The whole rule
/// is slide, else stop: the direction itself when it is open, for a blocked diagonal whichever of its two axis
/// steps is open, and otherwise nothing.
/// <para>The Z axis step is tested before the X axis step. When a diagonal's corner is blocked and both axes
/// are open there are two honest answers, and both heads have to pick the same one or every such step is a
/// misprediction. The order is arbitrary and FIXED, which is the only property it needs.</para>
/// <para>Pure over the collision map, so the simulator on both heads and any head that wants to preview a step
/// share one definition.</para>
/// </summary>
public static class TileSteerResolver
{
    /// <summary>The step to take from <paramref name="tile"/> while <paramref name="wanted"/> is held.</summary>
    /// <param name="map">The live collision map.</param>
    /// <param name="tile">The committed tile the body stands on, its south-west corner for a large body.</param>
    /// <param name="wanted">The held direction.</param>
    /// <param name="footprintSize">The body's square footprint size in tiles.</param>
    /// <returns>The direction to step, or null when the body stands.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="wanted"/> is outside the eight
    /// directions, which reaches <see cref="TileDirections.Delta"/> and is refused there. Loud rather than null on
    /// purpose: the decoder and <c>TileMoveSimulator.Accepts</c> are the gates a frame passes through, so a value
    /// this far out is an in-process misuse and a stand would hide it as a wall.</exception>
    public static TileDirection? Resolve(TileCollisionMap map, TileCoord tile, TileDirection wanted,
        int footprintSize)
    {
        if (Open(map, tile, wanted, footprintSize)) return wanted;
        (int dx, int dz) = TileDirections.Delta(wanted);
        if (dx == 0 || dz == 0) return null;

        TileDirection zAxis = dz > 0 ? TileDirection.N : TileDirection.S;
        if (Open(map, tile, zAxis, footprintSize)) return zAxis;
        TileDirection xAxis = dx > 0 ? TileDirection.E : TileDirection.W;
        return Open(map, tile, xAxis, footprintSize) ? xAxis : null;
    }

    static bool Open(TileCollisionMap map, TileCoord tile, TileDirection dir, int footprintSize) =>
        TileCollision.CanStep(map, tile.X, tile.Z, tile.Plane, dir, footprintSize);
}
