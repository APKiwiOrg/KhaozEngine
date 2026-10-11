using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.Tests.MapDocCompatibility;

internal enum LegacyWaterDifferenceKind { BoundaryOwner, FloorAboveSurface }
internal sealed record LegacyWaterDifference(LegacyWaterDifferenceKind Kind, string BodyId,
    MapExactXz Point, bool Legacy, bool Native);
internal sealed record LegacyWaterReport(int Bodies, int Rects, int Cells, int BoundarySamples,
    IReadOnlyList<LegacyWaterDifference> Differences, IReadOnlyList<string> Failures);

internal static class LegacyWaterComparison
{
    internal static LegacyWaterReport Compare(MapDocument native, IReadOnlyList<LegacyWaterSource> sources)
    {
        var differences = new List<LegacyWaterDifference>();
        var failures = new List<string>();
        int rects = sources.Sum(s => s.Body.Rects.Count);
        int cells = sources.Sum(s => s.Body.Rects.Sum(r => checked(r.Width * r.Height)));
        int boundarySamples = 0;
        if (native.Water is not { } water)
        {
            failures.Add("native water section is missing");
            return Report();
        }
        if (water.Bodies.Count != sources.Count || sources.Select(s => s.BodyId).Distinct(StringComparer.Ordinal).Count() != sources.Count)
            failures.Add("legacy and native body piece counts differ");
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(native.Surfaces);
        var query = new MapWaterQuery(view, water);
        foreach (LegacyWaterSource source in sources)
        {
            MapWaterBodyDoc[] matches = water.Bodies.Where(b => b.Id == source.BodyId).ToArray();
            if (matches.Length != 1)
            {
                failures.Add($"{source.BodyId}: expected one native body, found {matches.Length}");
                continue;
            }
            MapWaterBodyDoc body = matches[0];
            if (body.SurfaceY != source.Body.SurfaceY) failures.Add($"{source.BodyId}: level differs");
            MapWaterRect[] expectedRects = source.Body.Rects
                .Select(r => new MapWaterRect(r.X, r.Z, (long)r.X + r.Width, (long)r.Z + r.Height)).ToArray();
            if (body.Domain.Frame != MapLatticeFrame.ImportedMetreCentimetre || body.Domain.Rings.Count != 0 ||
                !body.Domain.Rects.SequenceEqual(expectedRects)) failures.Add($"{source.BodyId}: rectangle pieces differ");
            MapWaterDomain domain = MapWaterDomain.Compile(body.Domain);
            foreach (MapFramePoint point in CellCentres(source, 0f)) CompareCell(source, body, domain, point);
            foreach (MapExactXz point in BoundarySamples(source))
            {
                boundarySamples++;
                MapFramePoint framePoint = new(default, new(point.X.ToSingle(), 0f, point.Z.ToSingle()));
                if (framePoint.ExactWorldXz() != point)
                {
                    failures.Add($"{source.BodyId}: boundary point is not representable as a query input");
                    continue;
                }
                IReadOnlyList<MapSpaceInterval> intervals = MapSpaceIntervals.Column(view, framePoint);
                if (!Resolved(source, point, intervals)) continue;
                // Membership precedes the height test. A rim or a dry bed cannot change this ownership comparison.
                bool owns = intervals.Any(i => body.Covers.Any(c => c.Id == i.SpaceId)) && domain.Contains(point);
                bool legacy = LegacyContains(source, point);
                if (legacy != owns)
                    differences.Add(new(LegacyWaterDifferenceKind.BoundaryOwner, source.BodyId, point, legacy, owns));
            }
        }
        return Report();

        LegacyWaterReport Report() => new(sources.Count, rects, cells, boundarySamples,
            differences.AsReadOnly(), failures.AsReadOnly());

        bool Resolved(LegacyWaterSource source, MapExactXz point, IReadOnlyList<MapSpaceInterval> intervals)
        {
            foreach (MapSpaceInterval interval in intervals)
                if (interval.Status != MapMembershipStatus.Resolved)
                {
                    failures.Add($"{source.BodyId} at {point}: membership {interval.Status}, {interval.Detail}");
                    return false;
                }
            return true;
        }

        void CompareCell(LegacyWaterSource source, MapWaterBodyDoc body, MapWaterDomain domain, MapFramePoint point)
        {
            MapExactXz xz = point.ExactWorldXz();
            IReadOnlyList<MapWaterSample> column = query.Column(point);
            foreach (MapWaterSample sample in column)
                if (sample.Status != MapWaterStatus.Wet)
                {
                    failures.Add($"{source.BodyId} at {xz}: water column {sample.Status}, {sample.Detail}");
                    return;
                }
            if (column.Count == 1 && column[0].BodyId == source.BodyId)
            {
                MapWaterSample sample = column[0];
                if (!(sample.LowerY < sample.UpperY) || sample.NominalSurfaceY != source.Body.SurfaceY ||
                    sample.UpperY != source.Body.SurfaceY || !body.Covers.Any(c => c.Id == sample.SpaceId))
                    failures.Add($"{source.BodyId} at {xz}: wet interval or level differs");
                float midpoint = (float)(((double)sample.LowerY + sample.UpperY) * 0.5);
                if (midpoint > sample.LowerY && midpoint < sample.UpperY)
                {
                    MapWaterSample atMidpoint = query.Sample(point with { Local = point.Local with { Y = midpoint } });
                    if (atMidpoint.Status != MapWaterStatus.Wet || atMidpoint.BodyId != source.BodyId ||
                        atMidpoint.NominalSurfaceY != source.Body.SurfaceY)
                        failures.Add($"{source.BodyId} at {xz}: midpoint sample {atMidpoint.Status} in {atMidpoint.BodyId}");
                }
                return;
            }
            if (column.Count != 0)
            {
                failures.Add($"{source.BodyId} at {xz}: water column belongs to another body or has multiple intervals");
                return;
            }
            IReadOnlyList<MapSpaceInterval> intervals = MapSpaceIntervals.Column(view, point);
            if (!Resolved(source, xz, intervals)) return;
            MapSpaceInterval[] owned = intervals.Where(i => body.Covers.Any(c => c.Id == i.SpaceId)).ToArray();
            if (owned.Length == 1 && domain.Contains(xz) &&
                owned[0].Lower.CompareTo(MapExactValue.FromSingle(source.Body.SurfaceY)) >= 0)
                differences.Add(new(LegacyWaterDifferenceKind.FloorAboveSurface, source.BodyId, xz, true, false));
            else failures.Add($"{source.BodyId} at {xz}: expected a wet interval above the floor in its covered space");
        }
    }

    internal static IEnumerable<MapFramePoint> CellCentres(LegacyWaterSource source, float y)
    {
        foreach (var rect in source.Body.Rects)
            for (long z = rect.Z; z < (long)rect.Z + rect.Height; z++)
                for (long x = rect.X; x < (long)rect.X + rect.Width; x++)
                    yield return new(default, new Vector3(new MapExactValue(2 * x + 1, 2).ToSingle(), y,
                        new MapExactValue(-(2 * z + 1), 2).ToSingle()));
    }

    internal static IEnumerable<MapExactXz> BoundarySamples(LegacyWaterSource source)
    {
        foreach (var rect in source.Body.Rects)
        {
            MapExactValue x0 = new(rect.X, 1), x1 = new((long)rect.X + rect.Width, 1);
            MapExactValue z0 = new(-(long)rect.Z, 1), z1 = new(-((long)rect.Z + rect.Height), 1);
            MapExactValue midX = x0.Add(x1).Divide(new(2, 1)), midZ = z0.Add(z1).Divide(new(2, 1));
            yield return new(x0, z0);
            yield return new(x1, z0);
            yield return new(x0, z1);
            yield return new(x1, z1);
            yield return new(midX, z0);
            yield return new(midX, z1);
            yield return new(x0, midZ);
            yield return new(x1, midZ);
        }
    }

    static bool LegacyContains(LegacyWaterSource source, MapExactXz point)
    {
        MapExactValue tileZ = point.Z.Negate();
        return source.Body.Rects.Any(r => point.X.CompareTo(new(r.X, 1)) >= 0 &&
            point.X.CompareTo(new((long)r.X + r.Width, 1)) < 0 && tileZ.CompareTo(new(r.Z, 1)) >= 0 &&
            tileZ.CompareTo(new((long)r.Z + r.Height, 1)) < 0);
    }
}
