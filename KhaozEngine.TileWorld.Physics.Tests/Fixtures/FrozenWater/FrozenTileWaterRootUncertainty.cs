using System;
using I = KhaozEngine.Tests.TileWorld.Physics.Fixtures.FrozenTileWaterArithmetic;
using V = KhaozEngine.Tests.TileWorld.Physics.Fixtures.FrozenTileWaterConvexShape.Vector;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// Frozen from 418128539 for #1755. Do not synchronize with production optimisations.

/// <summary>Independent temporal charges for the two clamped quadratic root enclosures.</summary>
internal readonly record struct FrozenTileWaterRootUncertainty(I Entry, I Exit)
{
    internal static FrozenTileWaterRootUncertainty Clip(I entry, I exit, double low, double high) => new(
        new(Math.Clamp(entry.Low, low, high), Math.Clamp(entry.High, low, high)),
        new(Math.Clamp(exit.Low, low, high), Math.Clamp(exit.High, low, high)));

    internal double Error(double length) => Math.Max(Entry.High - Entry.Low, Exit.High - Exit.Low) * length;

    internal double CertifiedError(FrozenTileWaterBedSeam seam, int a, int b, int c, V start, V drive,
        I core, float radius, double length)
    {
        double entry = Entry.High - Entry.Low, exit = Exit.High - Exit.Low;
        // Outside these closed windows each root already determines its feature's classification.
        // A union contact inside one window cannot discharge the other root's uncertainty.
        if (entry > 0 && seam.Covers(a, b, c, start, drive, core, radius, Entry.Low, Entry.High)) entry = 0;
        if (exit > 0 && seam.Covers(a, b, c, start, drive, core, radius, Exit.Low, Exit.High)) exit = 0;
        return Math.Max(entry, exit) * length;
    }
}
