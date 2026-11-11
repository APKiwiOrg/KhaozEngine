using System;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>Independent temporal charges for the two clamped quadratic root enclosures.</summary>
internal readonly record struct TileWaterRootUncertainty(I Entry, I Exit)
{
    internal static TileWaterRootUncertainty Clip(I entry, I exit, double low, double high) => new(
        new(Math.Clamp(entry.Low, low, high), Math.Clamp(entry.High, low, high)),
        new(Math.Clamp(exit.Low, low, high), Math.Clamp(exit.High, low, high)));

    internal double Error(double length) => Math.Max(Entry.High - Entry.Low, Exit.High - Exit.Low) * length;

    internal double CertifiedError(TileWaterBedSeam seam, int a, int b, int c, V start, V drive,
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
