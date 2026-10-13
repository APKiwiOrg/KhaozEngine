using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

public sealed partial class TileMediumSampler
{
    readonly FieldSnapshot _field;
    public string SemanticIdentity => _field.SemanticIdentity;
    public long Generation => 0;
    public MovementWaterExtent Extent => _field.Extent;
    internal bool HasColumns => _field.Columns.Length > 0;

    public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin)
    {
        if (!body.IsValid || !float.IsFinite(physicsOrigin.X) || !float.IsFinite(physicsOrigin.Y) ||
            !float.IsFinite(physicsOrigin.Z) || body.CurrentSpace.WorldId != _field.WorldId)
            return new(MovementAvailability.Invalid, default, null, false, 0, null);
        // The binary64 quotient only proposes nearby tiles. Membership compares the exact absolute centre with
        // each candidate column's faces: X in [minimum, maximum), Z in (minimum, maximum] and feet in [bed, surface).
        double tileX = Math.Floor(((double)body.Centre.X + physicsOrigin.X) / _tileSize);
        double tileZ = Math.Floor(-((double)body.Centre.Z + physicsOrigin.Z) / _tileSize);
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                double x = tileX + dx, z = tileZ + dz;
                if (!(x >= int.MinValue && x <= int.MaxValue && z >= int.MinValue && z <= int.MaxValue) ||
                    !_field.ByTile.TryGetValue(((int)x, (int)z), out int index))
                    continue;
                TileWaterColumn column = _field.Columns[index];
                if (Contains(column, body, physicsOrigin))
                    return new(MovementAvailability.Known, body.CurrentSpace, column.Domain, true, 1, column.Interval);
            }
        return new(MovementAvailability.Known, body.CurrentSpace, null, false, 1, null);
    }

    static bool Contains(in TileWaterColumn column, in MovementBodyQuery body, Vector3 origin)
    {
        Vector3 centre = body.Centre;
        float half = -body.HalfHeight;
        return TileWaterArithmetic.Sign(centre.X, origin.X, -column.Minimum.X) >= 0 &&
            TileWaterArithmetic.Sign(centre.X, origin.X, -column.Maximum.X) < 0 &&
            TileWaterArithmetic.Sign(centre.Z, origin.Z, -column.Minimum.Z) > 0 &&
            TileWaterArithmetic.Sign(centre.Z, origin.Z, -column.Maximum.Z) <= 0 &&
            TileWaterArithmetic.Sign(centre.Y, origin.Y, half, -column.Minimum.Y) >= 0 &&
            TileWaterArithmetic.Sign(centre.Y, origin.Y, half, -column.Maximum.Y) < 0;
    }

    sealed class FieldSnapshot
    {
        internal string WorldId { get; }
        internal string SemanticIdentity { get; }
        internal TileWaterColumn[] Columns { get; }
        internal TileWaterColumn[] TraceColumns { get; }
        internal Dictionary<(int X, int Z), int> ByTile { get; } = new();
        internal TileWaterBoundary[] Boundaries { get; }
        internal TileWaterBoundary[] Exterior { get; }
        internal MovementWaterExtent Extent { get; }

        internal FieldSnapshot(TileWorldDocument document, TileWorldCatalogs catalogs, RegionCoord[] regions, string worldId)
        {
            WorldId = worldId;
            var columns = new List<TileWaterColumn>();
            using var semantic = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            semantic.AppendData(Encoding.UTF8.GetBytes($"tile-water-v1/{worldId.Length}/{worldId}/{Bits(document.TileSize)}\n"));
            foreach (RegionCoord region in regions.OrderBy(r => r.Rz).ThenBy(r => r.Rx))
                foreach (TileWaterBody body in TileWaterBodies.Collect(document, catalogs, region, Plane))
                {
                    var tiles = body.Rects.SelectMany(rect => Enumerable.Range(rect.Z, rect.Height)
                        .SelectMany(z => Enumerable.Range(rect.X, rect.Width).Select(x => (X: x, Z: z))))
                        .OrderBy(t => t.Z).ThenBy(t => t.X).ToArray();
                    string membership = string.Join("\n", tiles.Select(t => FormattableString.Invariant($"{t.X}/{t.Z}")));
                    string bodyId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(membership)));
                    string local = FormattableString.Invariant($"water/{region.Rx}/{region.Rz}/{bodyId}");
                    var domain = new MovementDomainKey(worldId, local);
                    foreach (var tile in tiles)
                    {
                        float bed = MathF.Min(MathF.Min(document.CornerHeight(tile.X, tile.Z, Plane), document.CornerHeight(tile.X + 1, tile.Z, Plane)),
                            MathF.Min(document.CornerHeight(tile.X, tile.Z + 1, Plane), document.CornerHeight(tile.X + 1, tile.Z + 1, Plane)));
                        if (!(bed < body.SurfaceY)) bed = MathF.BitDecrement(body.SurfaceY);
                        string face = FormattableString.Invariant($"{local}/column/{tile.X}/{tile.Z}");
                        var column = new TileWaterColumn(domain,
                            TileWorldSpace.ToWorld(tile.X, bed, tile.Z + 1, document.TileSize),
                            TileWorldSpace.ToWorld(tile.X + 1, body.SurfaceY, tile.Z, document.TileSize),
                            face + "/min-x", face + "/max-x", face + "/min-z", face + "/max-z", face + "/bed", face + "/surface");
                        ByTile.Add(tile, columns.Count);
                        columns.Add(column);
                        semantic.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{local}/{tile.X}/{tile.Z}/{Bits(bed)}/{Bits(body.SurfaceY)}\n")));
                    }
                }
            Columns = columns.ToArray();
            Vector3 min = Vector3.Zero, max = Vector3.Zero;
            for (int i = 0; i < Columns.Length; i++)
            {
                min = i == 0 ? Columns[i].Minimum : Vector3.Min(min, Columns[i].Minimum);
                max = i == 0 ? Columns[i].Maximum : Vector3.Max(max, Columns[i].Maximum);
            }
            Extent = new(min, max);
            SemanticIdentity = "tile-water-v1/" + Convert.ToHexString(semantic.GetHashAndReset());
            TraceColumns = TileWaterColumnUnion.Merge(Columns);
            Boundaries = TileWaterBoundary.Build(TraceColumns, sameDomainOnly: true);
            Exterior = TileWaterBoundary.Build(TraceColumns, sameDomainOnly: false);
        }

        static string Bits(float value) => BitConverter.SingleToInt32Bits(value).ToString("X8", CultureInfo.InvariantCulture);
    }
}
