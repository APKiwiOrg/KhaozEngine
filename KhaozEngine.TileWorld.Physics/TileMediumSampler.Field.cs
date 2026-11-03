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
        int index = _field.PointIndex.FindContaining(body, physicsOrigin);
        if (index >= 0)
        {
            TileWaterColumn column = _field.Columns[index];
            float x = body.Centre.X + physicsOrigin.X, z = body.Centre.Z + physicsOrigin.Z;
            if (!_field.Ground.TryDrawnHeight(x, z, out float bed))
                return new(MovementAvailability.Unresolved, default, null, false, 0, null);
            if (TileWaterArithmetic.Sign(body.Centre.Y, physicsOrigin.Y, -body.HalfHeight, -bed) >= 0 &&
                TileWaterArithmetic.Sign(body.Centre.Y, physicsOrigin.Y, -body.HalfHeight, -column.Maximum.Y) < 0)
                return new(MovementAvailability.Known, body.CurrentSpace, column.Domain, true, 1,
                    new(bed, column.Maximum.Y, column.Maximum.Y, true, column.BedBoundaryId, column.SurfaceBoundaryId));
        }
        return new(MovementAvailability.Known, body.CurrentSpace, null, false, 1, null);
    }

    sealed class FieldSnapshot
    {
        internal string WorldId { get; }
        internal string SemanticIdentity { get; }
        internal TileWaterColumn[] Columns { get; }
        internal CapturedTileGround Ground { get; }
        internal TileWaterPrism[] Prisms { get; }
        internal TileWaterPrismFace[] Faces { get; }
        internal int[] PrismStarts { get; }
        internal int[] FaceStarts { get; }
        internal TileWaterColumn[] FlatColumns { get; }
        internal TileWaterColumn[] NonuniformColumns { get; }
        internal TileWaterBoundary[] FlatBoundaries { get; }
        internal TileWaterBoundary[] FlatExterior { get; }
        internal TileWaterColumnIndex PointIndex { get; }
        internal MovementWaterExtent Extent { get; }

        internal FieldSnapshot(TileWorldDocument document, TileWorldCatalogs catalogs, RegionCoord[] regions, string worldId,
            TileMovementSnapshot captured)
        {
            WorldId = worldId;
            Ground = captured.Ground;
            var prisms = new List<TileWaterPrism>();
            var columns = new List<TileWaterColumn>();
            using var semantic = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            semantic.AppendData(Encoding.UTF8.GetBytes(Ground.Fingerprint));
            semantic.AppendData(Encoding.UTF8.GetBytes($"tile-water-drawn-v2/{worldId.Length}/{worldId}/{Bits(document.TileSize)}\n"));
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
                        bed = MathF.Min(bed, body.SurfaceY);
                        string face = FormattableString.Invariant($"{local}/column/{tile.X}/{tile.Z}");
                        var column = new TileWaterColumn(domain,
                            TileWorldSpace.ToWorld(tile.X, bed, tile.Z + 1, document.TileSize),
                            TileWorldSpace.ToWorld(tile.X + 1, body.SurfaceY, tile.Z, document.TileSize),
                            face + "/min-x", face + "/max-x", face + "/min-z", face + "/max-z", face + "/bed", face + "/surface");
                        columns.Add(column);
                        foreach (DrawnGroundTriangle triangle in Ground.Triangles(tile.X, tile.Z))
                        {
                            if (MathF.Min(triangle.A.Y, MathF.Min(triangle.B.Y, triangle.C.Y)) < body.SurfaceY)
                                prisms.Add(new(column, triangle, tile.X, tile.Z, document.TileSize, columns.Count - 1));
                            string geometry = FormattableString.Invariant($"{(int)triangle.PlanA}/{(int)triangle.PlanB}/{(int)triangle.PlanC}/{Bits(triangle.A.Y)}/{Bits(triangle.B.Y)}/{Bits(triangle.C.Y)}\n");
                            semantic.AppendData(Encoding.UTF8.GetBytes(geometry));
                        }
                        semantic.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{local}/{tile.X}/{tile.Z}/{Bits(bed)}/{Bits(body.SurfaceY)}\n")));
                    }
                }
            Columns = columns.ToArray();
            PointIndex = new(Columns);
            Vector3 min = Vector3.Zero, max = Vector3.Zero;
            for (int i = 0; i < Columns.Length; i++)
            {
                min = i == 0 ? Columns[i].Minimum : Vector3.Min(min, Columns[i].Minimum);
                max = i == 0 ? Columns[i].Maximum : Vector3.Max(max, Columns[i].Maximum);
            }
            var flat = new List<TileWaterColumn>();
            var nonuniform = new List<TileWaterColumn>();
            foreach (TileWaterColumn column in Columns)
            {
                if (TileWaterUniformBed.TryProve(Ground, column.Minimum, column.Maximum, out float bed))
                {
                    min.Y = Math.Min(min.Y, bed);
                    if (bed < column.Maximum.Y && column.Minimum.X < column.Maximum.X && column.Minimum.Z < column.Maximum.Z)
                        flat.Add(column with { Minimum = column.Minimum with { Y = bed } });
                }
                else nonuniform.Add(column);
            }
            FlatColumns = TileWaterColumnUnion.Merge(flat.ToArray());
            FlatBoundaries = TileWaterBoundary.Build(FlatColumns, true);
            FlatExterior = TileWaterBoundary.Build(FlatColumns, false);
            NonuniformColumns = nonuniform.ToArray();
            Extent = new(min, max);
            SemanticIdentity = "tile-water-drawn-v2/" + Convert.ToHexString(semantic.GetHashAndReset());
            Prisms = prisms.ToArray();
            Faces = TileWaterPrismFaces.Build(Prisms).OrderBy(f => Prisms[f.Prism].ColumnIndex).ThenBy(f => f.Prism).ToArray();
            PrismStarts = Starts(Columns.Length, Prisms.Select(p => p.ColumnIndex));
            FaceStarts = Starts(Columns.Length, Faces.Select(f => Prisms[f.Prism].ColumnIndex));
        }

        static int[] Starts(int columns, IEnumerable<int> owners)
        {
            var starts = new int[columns + 1];
            foreach (int owner in owners) starts[owner + 1]++;
            for (int i = 1; i < starts.Length; i++) starts[i] += starts[i - 1];
            return starts;
        }

        internal bool CanTraceFlat(in MovementMediumSweepQuery query, Vector3 origin)
        {
            var bounds = new TileWaterPathBounds(query, origin);
            foreach (TileWaterColumn column in NonuniformColumns)
                if (bounds.Meets(column.Minimum, column.Maximum)) return false;
            return true;
        }

        static string Bits(float value) => BitConverter.SingleToInt32Bits(value).ToString("X8", CultureInfo.InvariantCulture);
    }
}
