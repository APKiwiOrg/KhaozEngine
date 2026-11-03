using System.Collections.Generic;
using System;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>Immutable drawn triangles captured once for analytic support and water.</summary>
internal sealed class CapturedTileGround
{
    readonly Dictionary<(int X, int Z), DrawnGroundTriangle[]> _cells = new();
    readonly TileGroundSampler _sampler;
    internal float TileSize { get; }
    internal string Fingerprint { get; }

    internal CapturedTileGround(TileWorldDocument document, RegionCoord[] regions)
    {
        TileSize = document.TileSize;
        Span<DrawnGroundTriangle> triangles = stackalloc DrawnGroundTriangle[TileTriangulation.MaxTriangles];
        foreach (RegionCoord region in regions)
            for (int z = region.OriginZ; z < region.OriginZ + TileRegion.Size; z++)
                for (int x = region.OriginX; x < region.OriginX + TileRegion.Size; x++)
                {
                    int count = DrawnGroundTriangle.Describe(document, x, z, triangles);
                    if (count > 0) _cells.Add((x, z), triangles[..count].ToArray());
                }
        _sampler = new(document, regions, this);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"drawn-ground-v1/{BitConverter.SingleToInt32Bits(TileSize):X8}\n"));
        foreach (RegionCoord region in regions.OrderBy(r => r.Rz).ThenBy(r => r.Rx))
            hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"region/{region.Rx}/{region.Rz}\n")));
        foreach (var row in _cells.OrderBy(c => c.Key.Z).ThenBy(c => c.Key.X))
            foreach (DrawnGroundTriangle t in row.Value)
                hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant(
                    $"{row.Key.X}/{row.Key.Z}/{(int)t.PlanA}/{(int)t.PlanB}/{(int)t.PlanC}/{BitConverter.SingleToInt32Bits(t.A.Y)}/{BitConverter.SingleToInt32Bits(t.B.Y)}/{BitConverter.SingleToInt32Bits(t.C.Y)}\n")));
        Fingerprint = Convert.ToHexString(hash.GetHashAndReset());
    }

    internal ReadOnlySpan<DrawnGroundTriangle> Triangles(int x, int z) =>
        _cells.TryGetValue((x, z), out var triangles) ? triangles : [];
    internal float HeightAt(float x, float z) => _sampler.HeightAt(x, z);
    internal Vector3 NormalAt(float x, float z) => _sampler.NormalAt(x, z);
    internal bool TryDrawnHeight(float x, float z, out float height) => _sampler.TryDrawnHeight(x, z, out height);
    internal bool TryCoordinates(float x, float z, out float tileX, out float tileZ) =>
        _sampler.TryLoadedTile(x, z, out tileX, out tileZ);
}
