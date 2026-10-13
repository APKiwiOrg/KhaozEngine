using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

// Exact rectangular unions only. Equal intervals and owners may join across a whole
// shared edge. A concavity or a different bed always remains a separate finite box.
internal static class TileWaterColumnUnion
{
    readonly record struct Row(MovementDomainKey Domain, float Bottom, float Top, float MinZ, float MaxZ);
    readonly record struct Lane(MovementDomainKey Domain, float Bottom, float Top, float MinX, float MaxX);

    internal static TileWaterColumn[] Merge(TileWaterColumn[] columns)
    {
        var rows = new List<TileWaterColumn>();
        foreach (var group in columns.GroupBy(c => new Row(c.Domain, c.Minimum.Y, c.Maximum.Y, c.Minimum.Z, c.Maximum.Z)))
        {
            TileWaterColumn? pending = null;
            foreach (TileWaterColumn column in group.OrderBy(c => c.Minimum.X))
            {
                if (pending is { } prior && prior.Maximum.X == column.Minimum.X)
                    pending = Describe(prior.Domain, prior.Minimum, new(column.Maximum.X, prior.Maximum.Y, prior.Maximum.Z));
                else
                {
                    if (pending is { } complete) rows.Add(complete);
                    pending = column;
                }
            }
            if (pending is { } final) rows.Add(final);
        }
        var rectangles = new List<TileWaterColumn>();
        var open = new Dictionary<Lane, int>();
        foreach (TileWaterColumn row in rows.OrderBy(c => c.Minimum.Z).ThenBy(c => c.Minimum.X)
                     .ThenBy(c => c.Domain.LocalId, StringComparer.Ordinal).ThenBy(c => c.Minimum.Y))
        {
            var key = new Lane(row.Domain, row.Minimum.Y, row.Maximum.Y, row.Minimum.X, row.Maximum.X);
            if (open.TryGetValue(key, out int index) && rectangles[index].Maximum.Z == row.Minimum.Z)
            {
                var prior = rectangles[index];
                rectangles[index] = Describe(prior.Domain, prior.Minimum, new(prior.Maximum.X, prior.Maximum.Y, row.Maximum.Z));
            }
            else
            {
                open[key] = rectangles.Count;
                rectangles.Add(row);
            }
        }
        return rectangles.ToArray();
    }

    static TileWaterColumn Describe(MovementDomainKey domain, Vector3 min, Vector3 max)
    {
        string id = $"{domain.LocalId}/rectangle/{Bits(min.X)}/{Bits(min.Y)}/{Bits(min.Z)}/{Bits(max.X)}/{Bits(max.Y)}/{Bits(max.Z)}";
        return new(domain, min, max, id + "/min-x", id + "/max-x", id + "/min-z", id + "/max-z", id + "/bed", id + "/surface");
    }

    static string Bits(float value) => BitConverter.SingleToInt32Bits(value).ToString("X8", CultureInfo.InvariantCulture);
}
