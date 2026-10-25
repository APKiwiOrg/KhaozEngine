using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>Samples contained water against the occupied spaces of one acquisition. A point is wet in body B when
/// membership resolves space S, B covers S, B's domain contains the point's XZ and
/// <c>lowerY(S) &lt;= y &lt; min(SurfaceY, upperY(S))</c> in exact arithmetic. A tie between the level and the space's
/// upper bound closes the top. The query deep-copies the water set and fixes its digest at construction, so later
/// edits to the caller's set never change its answers.</summary>
public sealed class MapWaterQuery
{
    readonly MapScopedSurfaces _view;
    readonly CompiledBody[] _bodies;
    readonly MapWaterSet _snapshot;

    /// <summary>A body compiled once at construction. A body that cannot compile keeps its failure, which every read
    /// of a space it covers reports.</summary>
    sealed record CompiledBody(MapWaterBodyDoc Doc, MapWaterDomain? Domain, MapExactValue Surface, MapWaterStatus Failure, string? Detail)
    { internal MapCompiledWaterBody? Effective { get; init; } }

    public MapWaterQuery(MapScopedSurfaces scoped, MapWaterSet water)
    {
        ArgumentNullException.ThrowIfNull(scoped);
        ArgumentNullException.ThrowIfNull(water);
        _view = scoped;
        MapWaterSet snapshot = water.Clone();
        _snapshot = snapshot;
        _bodies = snapshot.Bodies.Select(Compile).ToArray();
        WaterDigest = _bodies.Any(b => b.Effective is not null) ? MapWaterJson.Hash("kemap/effective-water-query/1", w =>
        {
            w.WriteStartArray();
            w.WriteStringValue(snapshot.Digest());
            foreach (var body in _bodies.OrderBy(b => b.Doc.Id, StringComparer.Ordinal))
            { w.WriteStringValue(body.Doc.Id); w.WriteStringValue(body.Effective?.Digest ?? MapWaterSet.BodyDigest(body.Doc)); }
            w.WriteEndArray();
        }) : snapshot.Digest();
    }

    /// <summary>The digest of the water set captured at construction.</summary>
    public string WaterDigest { get; }

    /// <summary>The water state at <paramref name="point"/>. Membership failures keep their status, outside every
    /// space is dry, and more than one body containing the point in its space is ambiguous.</summary>
    public MapWaterSample Sample(MapFramePoint point)
    {
        MapSpaceInterval interval = MapSpaceIntervals.At(_view, point);
        if (interval.Status != MapMembershipStatus.Resolved)
        {
            if (interval.Status == MapMembershipStatus.MissingGeometry && _bodies.Any(b => b.Effective is not null) &&
                Unavailable(point.ExactWorldXz()) is { } unavailable)
                return Failure(point, unavailable.Failure, unavailable.Detail);
            return Failure(point, interval.Status, interval.Detail);
        }
        return Evaluate(point, interval, column: false, null);
    }

    /// <summary>Uses exact membership and water predicates. The frame point is presentation only.</summary>
    public MapWaterSample SampleExact(MapExactPoint world)
    {
        var point = MapEffectiveGeometry.Presentation(_view, world);
        var interval = MapSpaceIntervals.AtExact(_view, world);
        if (interval.Status == MapMembershipStatus.MissingGeometry && Unavailable(new(world.X, world.Z)) is { } unavailable)
            return Failure(point, unavailable.Failure, unavailable.Detail) with { ExactPoint = world };
        return (interval.Status != MapMembershipStatus.Resolved ? Failure(point, interval.Status, interval.Detail)
            : Evaluate(point, interval, column: false, world)) with
        { ExactPoint = world };
    }

    CompiledBody? Unavailable(MapExactXz xz) => _bodies.FirstOrDefault(b => b.Effective is { Status: not MapWaterCompileStatus.Complete }
        && (b.Domain is null || b.Domain.Contains(xz)));

    /// <summary>One wet sample per occupied interval at the point's XZ whose body holds water above that interval's
    /// floor, in ascending order, each with <see cref="MapFramePoint"/> height set to the floor. Any failure, from
    /// membership, an ambiguous body or a wet interval below float resolution, makes the whole column one entry
    /// with that status.</summary>
    public IReadOnlyList<MapWaterSample> Column(MapFramePoint point)
    {
        var samples = new List<MapWaterSample>();
        foreach (MapSpaceInterval interval in MapSpaceIntervals.Column(_view, point))
        {
            if (interval.Status != MapMembershipStatus.Resolved) return new[] { Failure(point, interval.Status, interval.Detail) };
            MapFramePoint floor = point with { Local = point.Local with { Y = interval.Lower.ToSingle() } };
            MapWaterSample sample = Evaluate(floor, interval, column: true, null);
            if (sample.Status is not (MapWaterStatus.Dry or MapWaterStatus.Wet)) return new[] { sample };
            if (sample.Status == MapWaterStatus.Wet) samples.Add(sample);
        }
        return samples.AsReadOnly();
    }

    /// <summary>Applies the wet rule to a resolved interval. A point read tests its own height, and a column read
    /// asks only whether the body holds water above the interval's floor.</summary>
    MapWaterSample Evaluate(MapFramePoint point, MapSpaceInterval interval, bool column, MapExactPoint? exact)
    {
        try
        {
            MapExactXz xz = exact is { } world ? new(world.X, world.Z) : point.ExactWorldXz();
            var height = column ? default : exact?.Y ?? point.ExactY();
            CompiledBody? found = null;
            foreach (CompiledBody body in _bodies)
            {
                if (!body.Doc.Covers.Any(c => c.Id == interval.SpaceId)) continue;
                if (body.Domain is null) return Failure(point, body.Failure, $"water body '{body.Doc.Id}': {body.Detail}");
                if (!body.Domain.Contains(xz)) continue;
                if (body.Effective is { Status: not MapWaterCompileStatus.Complete }) return Failure(point, body.Failure, body.Detail);
                if (found is not null) return Failure(point, MapWaterStatus.Ambiguous, "ambiguous water bodies");
                found = body;
            }
            if (found is null) return Dry(point, interval.SpaceId) with { Fragment = _bodies.Any(b => b.Effective is not null) ? interval.Fragment : null, ExactPoint = exact };
            bool free = interval.Upper is not { } upper || found.Surface.CompareTo(upper) < 0;
            MapExactValue top = free ? found.Surface : interval.Upper!.Value;
            bool wet = column ? interval.Lower.CompareTo(top) < 0
                : interval.Lower.CompareTo(height) <= 0 && height.CompareTo(top) < 0;
            if (!wet) return Dry(point, interval.SpaceId) with { Fragment = found.Effective is not null ? interval.Fragment : null, ExactPoint = exact };
            if (found.Effective is { } compiled && interval.Fragment is { } fragment)
            {
                var kind = compiled.ClassifyFragment(fragment.Id, xz);
                if (kind == MapWaterRegionKind.Unresolved)
                    return Failure(point, MapWaterStatus.MissingGeometry, $"water fragment '{fragment.Id}' is unresolved");
                if (kind is not null) free = kind == MapWaterRegionKind.FreeSurface;
            }
            float lowerY = interval.Lower.ToSingle(), upperY = free ? found.Doc.SurfaceY : top.ToSingle();
            if (lowerY >= upperY) return Failure(point, MapWaterStatus.NotRepresentable, "wet interval below float resolution");
            return new(MapWaterStatus.Wet, point, found.Doc.Id, interval.SpaceId, found.Doc.Medium, lowerY, upperY, found.Doc.SurfaceY,
                free, interval.LowerBoundaryId, free ? "water:" + found.Doc.Id : interval.UpperBoundaryId, null, _view.ReadWitness, WaterDigest)
            { Fragment = found.Effective is not null ? interval.Fragment : null, ExactPoint = exact };
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            return Failure(point, MapWaterStatus.NotRepresentable, "overflow");
        }
    }

    CompiledBody Compile(MapWaterBodyDoc body)
    {
        MapExactValue surface;
        try
        {
            surface = MapExactValue.FromSingle(body.SurfaceY);
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException or ArgumentException)
        {
            return new(body, null, default, MapWaterStatus.NotRepresentable, "surface not representable");
        }
        try
        {
            var effective = MapEffectiveWater.Resolve(_view, body);
            if (!MapEffectiveWater.UsesOccupied(_view, effective)) return new(body, effective.Domain, surface, default, null);
            var set = new MapWaterSet();
            set.Bodies.Add(body);
            // The captured medium participates in the full containment/compile path too.
            set.Media.AddRange(_snapshot.Media);
            var admission = _view.Scope.SceneAdmission ?? Scene.MapSceneAdmission.Current;
            using var admitted = admission?.Enter();
            using var preparation = admission?.BeginPreparation();
            var compiled = MapWaterCompiler.Compile(_view, set).Single();
            preparation?.Complete(new MapWaterInventory().Body(compiled));
            var status = compiled.Status switch
            {
                MapWaterCompileStatus.Incomplete => MapWaterStatus.MissingGeometry,
                MapWaterCompileStatus.CapacityExceeded => MapWaterStatus.CapacityExceeded,
                MapWaterCompileStatus.NotRepresentable => MapWaterStatus.NotRepresentable,
                MapWaterCompileStatus.Invalid => MapWaterStatus.Invalid,
                _ => default,
            };
            return new(body with { SurfaceY = effective.SurfaceY.ToSingle() }, effective.Domain, effective.SurfaceY, status, compiled.Detail)
            { Effective = compiled };
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            return new(body, null, default, MapWaterStatus.NotRepresentable, "domain not representable");
        }
        catch (MapDocumentException error)
        {
            MapWaterStatus status = error.Message.Contains("not representable", StringComparison.Ordinal) ? MapWaterStatus.NotRepresentable
                : error.Message.Contains("domain work exceeds", StringComparison.Ordinal) ? MapWaterStatus.CapacityExceeded
                : MapWaterStatus.Invalid;
            return new(body, null, default, status, error.Message);
        }
    }

    MapWaterSample Dry(MapFramePoint point, string? spaceId) => new(MapWaterStatus.Dry, point, null, spaceId, null,
        float.NaN, float.NaN, float.NaN, false, null, null, null, _view.ReadWitness, WaterDigest);

    MapWaterSample Failure(MapFramePoint point, MapMembershipStatus status, string? detail) => status == MapMembershipStatus.Outside
        ? Dry(point, null) : Failure(point, status switch
        {
            MapMembershipStatus.MissingGeometry => MapWaterStatus.MissingGeometry,
            MapMembershipStatus.Ambiguous => MapWaterStatus.Ambiguous,
            MapMembershipStatus.CapacityExceeded => MapWaterStatus.CapacityExceeded,
            MapMembershipStatus.NotRepresentable => MapWaterStatus.NotRepresentable,
            _ => MapWaterStatus.Invalid,
        }, detail);

    MapWaterSample Failure(MapFramePoint point, MapWaterStatus status, string? detail) => new(status, point, null, null, null,
        float.NaN, float.NaN, float.NaN, false, null, null, detail, _view.ReadWitness, WaterDigest);
}
