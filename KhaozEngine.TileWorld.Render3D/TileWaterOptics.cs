using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld.Render3D;

/// <summary>Null Plane tracks availability, including negative lookups. Other entries track height planes.</summary>
internal readonly record struct TileWaterOpticsDependency(RegionCoord Region, int? Plane);

/// <summary>A complete GPU-free field and mapping shared by compatible row-run rectangles.</summary>
internal sealed record TileWaterOpticsBuild(WaterOpticsField Field, Vector3 Origin, Vector2 Size,
    IReadOnlyList<TileWaterOpticsDependency> Dependencies);

/// <summary>Produces authored bilinear bed samples without loading regions or preparing scene resources.</summary>
internal static class TileWaterOptics
{
    internal static bool TryBuild(TileWorldDocument document, RegionCoord region, int plane,
        float referenceSurfaceY, float paddingMetres, out TileWaterOpticsBuild build,
        out TileWaterOpticsFailure failure)
    {
        ArgumentNullException.ThrowIfNull(document);
        if ((uint)plane >= (uint)document.PlaneCount) throw new ArgumentOutOfRangeException(nameof(plane));
        if (!float.IsFinite(referenceSurfaceY)) throw new ArgumentOutOfRangeException(nameof(referenceSurfaceY));
        if (!float.IsFinite(paddingMetres) || paddingMetres < 0f)
            throw new ArgumentOutOfRangeException(nameof(paddingMetres));
        float tileSize = document.TileSize;
        if (!float.IsFinite(tileSize) || tileSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(document), "Tile size must be finite and positive.");

        build = null!;
        failure = new TileWaterOpticsFailure(region, plane, referenceSurfaceY, TileWaterOpticsFailureKind.FieldTooLarge);
        // Round displacement outward on the authored lattice, then add one interpolation cell.
        // Check in double before narrowing, so even a finite extreme padding cannot wrap or downsample.
        double margin = Math.Ceiling((double)paddingMetres / tileSize) + 1;
        double dimension = TileRegion.Size + 1 + 2 * margin;
        if (dimension > WaterOpticsField.MaxDimension) return false;
        int cells = (int)margin;
        int samples = (int)dimension;
        long west = (long)region.Rx * TileRegion.Size - cells;
        long south = (long)region.Rz * TileRegion.Size - cells;
        long east = (long)region.Rx * TileRegion.Size + TileRegion.Size + cells;
        long north = (long)region.Rz * TileRegion.Size + TileRegion.Size + cells;
        if (west < int.MinValue || south < int.MinValue || east > int.MaxValue || north > int.MaxValue)
            return false;

        int minX = (int)west, maxZ = (int)north;
        Vector3 origin = TileWorldSpace.ToWorld(minX, referenceSurfaceY, maxZ, tileSize);
        float extent = TileWorldSpace.WorldX(samples - 1, tileSize);
        float farX = TileWorldSpace.WorldX((int)east, tileSize);
        float farZ = TileWorldSpace.WorldZ((int)south, tileSize);
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Z) || !float.IsFinite(extent)
            || !float.IsFinite(farX) || !float.IsFinite(farZ) || extent <= 0f) return false;
        if (!TileWaterOpticsMapping.IsRepresentable(minX, maxZ, samples, tileSize, origin, extent)) return false;

        var depths = new float[checked(samples * samples)];
        var dependencies = new List<TileWaterOpticsDependency>();
        var unique = new HashSet<TileWaterOpticsDependency>();
        Span<RegionCoord> inspected = stackalloc RegionCoord[4];
        for (int row = 0; row < samples; row++)
        {
            // TileWorldSpace puts the north edge at minimum world Z. Increasing world-Z rows
            // therefore walk south through decreasing authored tile Z, without resampling heights.
            int z = maxZ - row;
            for (int column = 0; column < samples; column++)
            {
                if (!document.TryReadCornerHeight(minX + column, z, plane, inspected, out int count,
                    out TileCornerHeightRead read, out RegionCoord missing))
                {
                    failure = new TileWaterOpticsFailure(region, plane, referenceSurfaceY,
                        TileWaterOpticsFailureKind.MissingHeightData, missing);
                    return false;
                }
                for (int i = 0; i < count; i++)
                    AddDependency(new TileWaterOpticsDependency(inspected[i], null));
                // The requested plane matters even when derived today: authoring it later changes
                // the source. The resolved base plane independently matters for edits to that lift.
                AddDependency(new TileWaterOpticsDependency(read.Owner, plane));
                AddDependency(new TileWaterOpticsDependency(read.Owner, read.SourcePlane));
                depths[row * samples + column] = referenceSurfaceY - read.Height;
            }
        }
        build = new TileWaterOpticsBuild(new WaterOpticsField(samples, samples, depths), origin,
            new Vector2(extent), dependencies.AsReadOnly());
        failure = default;
        return true;

        void AddDependency(TileWaterOpticsDependency dependency)
        {
            if (unique.Add(dependency)) dependencies.Add(dependency);
        }
    }
}
