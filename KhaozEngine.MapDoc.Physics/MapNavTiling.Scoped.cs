using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc.Physics;

public static partial class MapNavTiling
{
    /// <summary>Partitions the scoped simulation domain only after every padded capture is acquired.</summary>
    public static IReadOnlyList<MapNavTile> Partition(MapScopedWorld world, MapWorldGrids grids, MapNavTileOptions options)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.RequireAlive();
        ArgumentNullException.ThrowIfNull(grids);
        ValidateOptions(options);
        grids.ValidateDomain(world.TileSize, world.Bounds);
        (int minX, int maxX) = TileAxis(world.Bounds.MinX, world.Bounds.MaxX, grids.Origin.X, grids.NavTileSize, "X");
        (int minZ, int maxZ) = TileAxis(world.Bounds.MinZ, world.Bounds.MaxZ, grids.Origin.Y, grids.NavTileSize, "Z");
        long count = RequirePartitionCount(minX, maxX, minZ, maxZ);
        var acquired = world.Scope.Surfaces.WorldRectangle();
        var first = Grow(TileBounds(new(minX, minZ), grids, world.Bounds.MinY, world.Bounds.MaxY), options.SeamMarginMetres);
        var last = Grow(TileBounds(new(maxX, maxZ), grids, world.Bounds.MinY, world.Bounds.MaxY), options.SeamMarginMetres);
        if (first.MinX < acquired.MinX.ToDouble() || first.MinZ < acquired.MinZ.ToDouble() ||
            last.MaxX > acquired.MaxX.ToDouble() || last.MaxZ > acquired.MaxZ.ToDouble() ||
            world.Bounds.MinY < world.Scope.Surfaces.MinY || world.Bounds.MaxY > world.Scope.Surfaces.MaxY)
            throw new MapDocumentException(MapDocumentRefusal.AcquisitionIncomplete, "Navigation capture seam margins are outside acquired simulation coverage.") { Operation = "Navigation.Capture" };
        try
        {
            using var admitted = world.Admission.Enter();
            using var preparation = world.Admission.BeginPreparation();
            preparation.Reserve(128);
            var result = PartitionDescriptors(world.Bounds, world.Statics, Array.Empty<MapLegacySculptTile>(), world.Water, true, "",
                grids, options, minX, maxX, minZ, maxZ, count);
            world.Retain(preparation.Complete(checked(128L + 512L * result.Count)));
            return result;
        }
        catch (MapSceneCapacityException error) { throw MapDocumentRefusalFactory.FromFinding(error.Capacity.Finding, "navigation capacity: " + error.Message, error); }
    }

    /// <summary>Seam descriptors use the same geometry identity kernel as complete worlds.</summary>
    public static IReadOnlyList<MapNavSeam> Seams(IReadOnlyList<MapNavTile> tiles, MapScopedWorld world, MapNavTileOptions options)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(world);
        world.RequireAlive();
        ValidateOptions(options);
        try
        {
            using var admitted = world.Admission.Enter();
            using var preparation = world.Admission.BeginPreparation();
            preparation.Reserve(checked(128L + 512L * tiles.Count));
            var result = SeamDescriptors(tiles, world.Bounds, options);
            world.Retain(preparation.Complete(checked(128L + 256L * result.Count)));
            return result;
        }
        catch (MapSceneCapacityException error) { throw MapDocumentRefusalFactory.FromFinding(error.Capacity.Finding, "navigation capacity: " + error.Message, error); }
    }

    /// <summary>Cross-tile topology from the retained scoped surface and record witness.</summary>
    public static IReadOnlyList<MapNavLink> Links(MapScopedWorld world, MapWorldGrids grids)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.RequireAlive();
        ArgumentNullException.ThrowIfNull(grids);
        grids.ValidateDomain(world.TileSize, world.Bounds);
        try
        {
            using var admitted = world.Admission.Enter();
            using var preparation = world.Admission.BeginPreparation();
            preparation.Reserve(128);
            var result = LinkDescriptors(world.Surfaces, grids);
            world.Retain(preparation.Complete(checked(128L + 256L * result.Count)));
            return result;
        }
        catch (MapSceneCapacityException error) { throw MapDocumentRefusalFactory.FromFinding(error.Capacity.Finding, "navigation capacity: " + error.Message, error); }
    }
}
