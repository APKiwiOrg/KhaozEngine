using System;
using System.Numerics;

namespace KhaozEngine.TileWorld.Render3D;

/// <summary>Admits float-origin fields only at the precision of their bounded local lattice.</summary>
internal static class TileWaterOpticsMapping
{
    // Binary32 round-to-nearest unit roundoff. The binding uses four roundings: reciprocal extent,
    // origin subtraction, reciprocal multiplication and multiplication by the lattice cell count.
    const double UnitRoundoff = 1d / (1 << 24);
    const double Gamma4 = 4 * UnitRoundoff / (1 - 4 * UnitRoundoff);

    internal static bool IsRepresentable(int minX, int maxZ, int samples, float tileSize,
        Vector3 origin, float extent)
    {
        float inverseExtent = 1f / extent;
        if (!float.IsFinite(inverseExtent) || inverseExtent <= 0f) return false;
        int cells = samples - 1;
        double cellTolerance = Gamma4 * cells;
        return Axis(minX, 1, false, origin.X) && Axis(maxZ, -1, true, origin.Z);

        bool Axis(int first, int step, bool worldZ, float anchor)
        {
            float worldPerTile = worldZ ? TileWorldSpace.WorldZ(1f, tileSize) : TileWorldSpace.WorldX(1f, tileSize);
            double worldTolerance = cellTolerance * tileSize;
            float previous = 0f;
            for (int i = 0; i < samples; i++)
            {
                int tile = first + step * i;
                float tileFloat = tile;
                // TileWorldSpace accepts float tile coordinates. Do not lose the integer before multiplying.
                if ((double)tileFloat != tile) return false;
                float world = worldZ ? TileWorldSpace.WorldZ(tileFloat, tileSize) : TileWorldSpace.WorldX(tileFloat, tileSize);
                if (!float.IsFinite(world) || (i > 0 && world <= previous)) return false;
                previous = world;

                // The exactly representable integer times binary32 tile size is exact in binary64.
                // Check both authored world vertices and the entire stored affine mapping. Its error
                // between vertices is linear, so no interior interval can conceal a shifted anchor.
                double authoredWorld = tile * (double)worldPerTile;
                double mappedWorld = anchor + i * (double)extent / cells;
                if (Math.Abs(world - authoredWorld) > worldTolerance
                    || Math.Abs(mappedWorld - authoredWorld) > worldTolerance) return false;

                // Pin both the CPU reference division and the shader's uploaded reciprocal path.
                // The error budget is in local cells and never grows with the absolute world position.
                float local = world - anchor;
                float cpuIndex = (local / extent) * cells;
                float shaderIndex = (local * inverseExtent) * cells;
                if (!float.IsFinite(cpuIndex) || !float.IsFinite(shaderIndex)
                    || Math.Abs((double)cpuIndex - i) > cellTolerance
                    || Math.Abs((double)shaderIndex - i) > cellTolerance) return false;
            }
            return true;
        }
    }
}
