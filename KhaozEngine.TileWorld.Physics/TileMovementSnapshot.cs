using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.TileWorld.Physics;

// The captured document is private to its sampler and never escapes to an editor or a caller.
internal sealed class TileMovementSnapshot
{
    readonly CapturedTileGround _ground;
    internal CapturedTileGround Ground => _ground;
    readonly TileWorldDocument _captured;
    readonly TileGroundBounds _bounds;
    internal float MinimumY { get; }
    internal float MaximumY { get; }

    internal TileMovementSnapshot(TileWorldDocument document, RegionCoord[] regions)
    {
        _captured = new() { TileSize = document.TileSize, PlaneCount = document.PlaneCount, PlaneHeight = document.PlaneHeight };
        foreach (RegionCoord coord in regions)
        {
            TilePlaneData from = document.GetRegion(coord)!.Plane(0), to = _captured.GetOrCreateRegion(coord).Plane(0);
            to.Heights = from.Heights?.ToArray();
            to.Underlay = from.Underlay?.ToArray();
            to.Overlay = from.Overlay?.ToArray();
            to.OverlayShape = from.OverlayShape?.ToArray();
            to.OverlayRotation = from.OverlayRotation?.ToArray();
            to.Settings = from.Settings?.ToArray();
        }
        _ground = new(_captured, regions);
        _bounds = new(regions);
        float min = 0, max = 0;
        foreach (RegionCoord coord in regions)
            for (int z = coord.OriginZ; z <= coord.OriginZ + TileRegion.Size; z++)
                for (int x = coord.OriginX; x <= coord.OriginX + TileRegion.Size; x++)
                {
                    float height = _captured.CornerHeight(x, z, 0);
                    min = MathF.Min(min, height);
                    max = MathF.Max(max, height);
                }
        MinimumY = MathF.BitDecrement(min);
        MaximumY = MathF.BitIncrement(max);
    }

    internal float Height(float x, float z) => Resident(x, z) ? _ground.HeightAt(x, z) : float.NaN;
    internal Vector3 Normal(float x, float z) => Resident(x, z) ? _ground.NormalAt(x, z) : new(float.NaN);

    bool Resident(float x, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z)) return false;
        if (!_bounds.TryMap(x, z, _captured.TileSize, out float tx, out float tz)) return false;
        if (tx < int.MinValue || tx >= int.MaxValue || tz < int.MinValue || tz >= int.MaxValue) return false;
        return _captured.GetRegion(RegionCoord.Of((int)MathF.Floor(tx), (int)MathF.Floor(tz))) is not null;
    }

    internal static string ContentWorldId(TileWorldDocument document, RegionCoord[] regions)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string text) => digest.AppendData(Encoding.UTF8.GetBytes(text + "\n"));
        Append(BitConverter.SingleToInt32Bits(document.TileSize).ToString("X8", CultureInfo.InvariantCulture));
        foreach (RegionCoord coord in regions.OrderBy(c => c.Rz).ThenBy(c => c.Rx))
        {
            Append(FormattableString.Invariant($"{coord.Rx}/{coord.Rz}"));
            for (int z = coord.OriginZ; z < coord.OriginZ + TileRegion.Size; z++)
                for (int x = coord.OriginX; x < coord.OriginX + TileRegion.Size; x++)
                    Append(FormattableString.Invariant($"{document.CornerHeightCm(x, z, 0)}/{document.GetUnderlay(x, z, 0)}/{document.GetOverlay(x, z, 0)}/{document.GetOverlayShape(x, z, 0)}/{document.GetOverlayRotation(x, z, 0)}/{document.GetSettings(x, z, 0)}"));
            foreach (TileObject o in document.GetRegion(coord)!.Objects.OrderBy(o => o.Id))
                Append(FormattableString.Invariant($"{o.Id}/{o.ArchetypeId.Length}/{o.ArchetypeId}/{o.Plane}/{o.X}/{o.Z}/{o.Rotation}"));
        }
        return "tile-content-v1/" + Convert.ToHexString(digest.GetHashAndReset());
    }
}
