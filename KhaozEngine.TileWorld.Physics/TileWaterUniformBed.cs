using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using F = KhaozEngine.TileWorld.Physics.DrawnGroundEvaluation.Range;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>Proves a rectangular trace specialization over the captured sampler's reachable coordinate ranges.</summary>
internal static class TileWaterUniformBed
{
    internal static bool TryProve(CapturedTileGround ground, Vector3 minimum, Vector3 maximum, out float bed)
    {
        bed = 0;
        if (!Axis(ground, minimum, maximum, true, out var xs) || !Axis(ground, minimum, maximum, false, out var zs)) return false;
        bool found = false;
        foreach (var z in zs)
            foreach (var x in xs)
            {
                ReadOnlySpan<DrawnGroundTriangle> triangles = ground.Triangles(x.Key, z.Key);
                if (triangles.IsEmpty) return false;
                foreach (DrawnGroundTriangle triangle in triangles)
                {
                    var (b, c) = DrawnGroundEvaluation.Weights(x.Value, z.Value, triangle);
                    F height = DrawnGroundEvaluation.Height(b, c, triangle);
                    if (height.Low != height.High || found && height.Low != bed) return false;
                    bed = height.Low;
                    found = true;
                }
            }
        return found;
    }

    static bool Axis(CapturedTileGround ground, Vector3 minimum, Vector3 maximum, bool xAxis, out Dictionary<int, F> cells)
    {
        cells = [];
        float low = xAxis ? minimum.X : minimum.Z, high = xAxis ? maximum.X : maximum.Z;
        long floats = (long)Ordered(high) - Ordered(low) + 1;
        if (floats <= MovementQueryLease.MaxDomainContacts)
        {
            // Every distinct represented input is checked once. The resulting ranges can include extra inputs,
            // making this proof stronger, while never omitting a coordinate the actual sampler can reach.
            for (float coordinate = low; ; coordinate = MathF.BitIncrement(coordinate))
            {
                if (!Coordinate(ground, minimum, xAxis, coordinate, out int cell, out float local)) return false;
                if (cells.TryGetValue(cell, out F range)) cells[cell] = new(MathF.Min(range.Low, local), MathF.Max(range.High, local));
                else cells.Add(cell, F.From(local));
                if (coordinate == high) break;
            }
            return true;
        }
        if (!Coordinate(ground, minimum, xAxis, low, out int first, out float firstLocal) ||
            !Coordinate(ground, minimum, xAxis, high, out int last, out float lastLocal)) return false;
        if (first > last) { (first, last) = (last, first); (firstLocal, lastLocal) = (lastLocal, firstLocal); }
        if ((long)last - first + 1 > MovementQueryLease.MaxDomainContacts) return false;
        for (long cell = first; cell <= last; cell++)
            cells.Add((int)cell, new(cell == first ? firstLocal : 0, cell == last ? lastLocal : 1));
        return true;
    }

    static bool Coordinate(CapturedTileGround ground, Vector3 reference, bool xAxis, float value, out int cell, out float local)
    {
        cell = 0;
        local = 0;
        if (!ground.TryCoordinates(xAxis ? value : reference.X, xAxis ? reference.Z : value, out float x, out float z)) return false;
        float coordinate = xAxis ? x : z;
        double floor = Math.Floor(coordinate);
        if (floor < int.MinValue || floor > int.MaxValue) return false;
        cell = (int)floor;
        local = coordinate - cell;
        return true;
    }

    static uint Ordered(float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        return bits < 0 ? (uint)~bits : (uint)(bits ^ int.MinValue);
    }
}
