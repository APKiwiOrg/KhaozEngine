using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.MapDocOracle;

internal sealed record InventoryRegion(int X, int Z);
internal sealed record PlaneInventory(int Plane, long DrawableCells, long OverlayCells, long VoidCells,
    long NoDrawCells, long AuthoredUpperPlaneCorners, long DistinctCorners,
    IReadOnlyDictionary<string, long> AuthoredCuts, IReadOnlyDictionary<string, long> OperativeCuts,
    IReadOnlyDictionary<string, long> FlagBits);

internal sealed record ShippedSourceInventory(IReadOnlyList<InventoryRegion> Regions, IReadOnlyList<PlaneInventory> Planes)
{
    // The world root is the directory of the single attested world manifest under the verified extraction root, even
    // when git archive kept its directory prefix.
    internal static string WorldRoot(PrivateOracleInputs inputs)
    {
        ShippedSourcePath manifest = inputs.Provenance.Paths.Single(p =>
            Path.GetFileName(p.Path) == TileWorldFile.ManifestFileName);
        return Path.GetDirectoryName(Path.Combine(inputs.SourceRoot, manifest.Path))!;
    }

    // The verified world's regions in signed region order, one of them carrying the run's spike-point outcome.
    internal static IReadOnlyList<ShippedRegion> ReadRegions(PrivateOracleInputs inputs)
        => ShippedRegion.All(TileWorldFile.Load(WorldRoot(inputs)), inputs.Provenance.Comparison);

    internal static ShippedSourceInventory Build(string root)
    {
        TileWorldDocument document = TileWorldFile.Load(root);
        var regions = document.Regions.Values.OrderBy(r => r.Coord.Rz).ThenBy(r => r.Coord.Rx).ToArray();
        var planes = new List<PlaneInventory>();
        var triangles = new TileLatticeTriangle[TileTriangulation.MaxTriangles];
        for (int plane = 0; plane < document.PlaneCount; plane++)
        {
            long drawable = 0, overlays = 0, voids = 0, noDraw = 0, authored = 0;
            var corners = new HashSet<(int X, int Z)>();
            var rawCuts = new Dictionary<string, long>(StringComparer.Ordinal);
            var operativeCuts = new Dictionary<string, long>(StringComparer.Ordinal);
            var flags = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (TileOverlayShape cut in Enum.GetValues<TileOverlayShape>())
            {
                rawCuts.Add(cut.ToString(), 0);
                operativeCuts.Add(cut.ToString(), 0);
            }
            for (int bit = 0; bit < 8; bit++) flags.Add("bit-" + bit, 0);

            foreach (TileRegion region in regions)
            {
                // Authored means stored entries, including explicit zero overrides, not only nonzero heights.
                if (plane > 0 && region.Planes[plane].Heights is { } heights) authored += heights.LongLength;
                for (int z = 0; z < TileRegion.Size; z++)
                    for (int x = 0; x < TileRegion.Size; x++)
                    {
                        int worldX = checked(region.Coord.Rx * TileRegion.Size + x);
                        int worldZ = checked(region.Coord.Rz * TileRegion.Size + z);
                        ushort underlay = document.GetUnderlay(worldX, worldZ, plane);
                        ushort overlay = document.GetOverlay(worldX, worldZ, plane);
                        TileSettings settings = document.GetSettings(worldX, worldZ, plane);
                        if (overlay != 0) overlays++;
                        if (underlay == 0) voids++;
                        if ((settings & TileSettings.NoDraw) != 0) noDraw++;
                        string raw = document.GetOverlayShape(worldX, worldZ, plane).ToString();
                        rawCuts[raw] = rawCuts.GetValueOrDefault(raw) + 1;
                        for (int bit = 0; bit < 8; bit++)
                            if (((byte)settings & (1 << bit)) != 0) flags["bit-" + bit]++;
                        if (TileGroundTriangles.TryDescribe(document, worldX, worldZ, plane, out TileGroundCell cell, triangles))
                        {
                            drawable++;
                            string operative = cell.Cut.ToString();
                            operativeCuts[operative] = operativeCuts.GetValueOrDefault(operative) + 1;
                        }
                        corners.Add((worldX, worldZ));
                        corners.Add((checked(worldX + 1), worldZ));
                        corners.Add((worldX, checked(worldZ + 1)));
                        corners.Add((checked(worldX + 1), checked(worldZ + 1)));
                    }
            }
            planes.Add(new(plane, drawable, overlays, voids, noDraw, authored, corners.Count,
                rawCuts, operativeCuts, flags));
        }
        return new(Array.AsReadOnly(regions.Select(r => new InventoryRegion(r.Coord.Rx, r.Coord.Rz)).ToArray()),
            planes.AsReadOnly());
    }
}
