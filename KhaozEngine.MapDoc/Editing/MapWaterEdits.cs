using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Editing;

public abstract record MapWaterEdit(string Label);
public sealed record MapUpsertWaterBody(MapWaterBodyDoc Body) : MapWaterEdit("Set water body");
public sealed record MapRemoveWaterBody(string BodyId) : MapWaterEdit("Remove water body");
public sealed record MapUpsertWaterMedium(MapWaterMediumDoc Medium) : MapWaterEdit("Set water medium");
public sealed record MapRemoveWaterMedium(string Key) : MapWaterEdit("Remove water medium");
public sealed record MapSetBoundedWater(bool Bounded) : MapWaterEdit("Set bounded water");
public sealed record MapWaterEditResult(MapWaterSet? After, MapNativeWriteSet WriteSet, MapNativeEditEffects Effects);

/// <summary>Pure water preparation. Containment is checked against the complete document at publication.</summary>
public static class MapWaterEdits
{
    const MapNativeInvalidation BodyInvalidation = MapNativeInvalidation.Medium | MapNativeInvalidation.Residency |
        MapNativeInvalidation.Nav;

    public static MapWaterEditResult Prepare(MapDocument document, MapWaterEdit edit)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(edit);
        MapWaterSet? after = document.Water?.Clone();
        switch (edit)
        {
            case MapSetBoundedWater bounded:
                if (bounded.Bounded)
                {
                    if (document.ResolverIdentity is not { PayloadVersion: 1, ResolverVersion: 2 } ||
                        document.SupportRecipe != MapSupportRecipe.AuthoredBindingsV2)
                        throw new MapDocumentException("bounded water requires a resolver-2 native document");
                    after ??= new MapWaterSet();
                }
                else
                {
                    if (after is { Bodies.Count: > 0 })
                        throw new MapDocumentException("remove every water body before using legacy global water");
                    after = null;
                }
                break;
            case MapUpsertWaterBody upsert:
                ArgumentNullException.ThrowIfNull(upsert.Body);
                MapWaterSet bodies = RequireBounded(after);
                int bodyIndex = bodies.Bodies.FindIndex(b => b.Id == upsert.Body.Id);
                if (bodyIndex >= 0) bodies.Bodies[bodyIndex] = upsert.Body;
                else bodies.Bodies.Add(upsert.Body);
                break;
            case MapRemoveWaterBody remove:
                MapWaterSet remaining = RequireBounded(after);
                int removeIndex = remaining.Bodies.FindIndex(b => b.Id == remove.BodyId);
                if (removeIndex < 0) throw new MapDocumentException($"no water body '{remove.BodyId}'");
                remaining.Bodies.RemoveAt(removeIndex);
                break;
            case MapUpsertWaterMedium upsert:
                ArgumentNullException.ThrowIfNull(upsert.Medium);
                MapWaterSet media = RequireBounded(after);
                int mediumIndex = media.Media.FindIndex(m => m.Key == upsert.Medium.Key);
                if (mediumIndex >= 0) media.Media[mediumIndex] = upsert.Medium;
                else media.Media.Add(upsert.Medium);
                break;
            case MapRemoveWaterMedium remove:
                MapWaterSet unused = RequireBounded(after);
                string? user = unused.Bodies.Where(b => b.Medium == remove.Key)
                    .Select(b => b.Id).Order(StringComparer.Ordinal).FirstOrDefault();
                if (user is not null) throw new MapDocumentException($"water medium '{remove.Key}' is used by '{user}'");
                int unusedIndex = unused.Media.FindIndex(m => m.Key == remove.Key);
                if (unusedIndex < 0) throw new MapDocumentException($"no water medium '{remove.Key}'");
                unused.Media.RemoveAt(unusedIndex);
                break;
            default: throw new MapDocumentException("unknown water edit type");
        }
        // Upserts can carry caller-owned nested collections. Detach them before validation or returning the set.
        after = after?.Clone();
        var errors = new List<string>();
        MapWaterValidation.Validate(new MapDocument
        {
            ResolverIdentity = document.ResolverIdentity,
            SupportRecipe = document.SupportRecipe,
            Water = after,
        }, errors);
        if (errors.Count != 0) throw new MapDocumentException(errors[0]);
        MapNativeEditEffects effects = Describe(document.Water, after, edit);
        MapNativeWriteSet writes = MapNativeWriteSet.PlacementsOnly with { Placements = false, Water = true };
        return new(after, writes, effects);
    }

    /// <summary>Adds the water bodies whose covered spaces meet the effects, resolving affected aliases through
    /// <paramref name="surfaces"/> to their owners and preserving all other consequences.</summary>
    public static MapNativeEditEffects NameAffectedBodies(MapWaterSet? water, MapNativeEditEffects effects, MapSurfaceSet surfaces)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(surfaces);
        if (water is null) return effects;
        var owners = new HashSet<string>(effects.SpaceIds, StringComparer.Ordinal);
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(surfaces);
        foreach (MapSpaceDoc space in surfaces.AllRecords().OfType<MapSpaceDoc>().Where(s => owners.Contains(s.Id)).ToArray())
            owners.Add(MapSpaceGeometry.Owner(view, space));
        string[] affected = water.Bodies.Where(b => b.Covers.Any(c => owners.Contains(c.Id)))
            .Select(b => b.Id).ToArray();
        if (affected.Length == 0) return effects;
        return effects with
        {
            WaterBodyIds = Ids(effects.WaterBodyIds.Concat(affected)),
            Invalidates = effects.Invalidates | MapNativeInvalidation.Medium,
        };
    }

    static MapWaterSet RequireBounded(MapWaterSet? water) => water ?? throw new MapDocumentException("bounded water is off");

    static MapNativeEditEffects Describe(MapWaterSet? before, MapWaterSet? after, MapWaterEdit edit)
    {
        if (edit is MapSetBoundedWater)
            return Effects(null, null, Array.Empty<MapWaterBodyDoc>(), Array.Empty<MapWaterBodyDoc>(),
                Array.Empty<MapDigestChange>(), MapNativeInvalidation.Medium | MapNativeInvalidation.Unbounded);
        string? bodyId = edit switch { MapUpsertWaterBody upsert => upsert.Body.Id, MapRemoveWaterBody remove => remove.BodyId, _ => null };
        if (bodyId is not null)
        {
            MapWaterBodyDoc? oldBody = before!.Bodies.Find(b => b.Id == bodyId), newBody = after!.Bodies.Find(b => b.Id == bodyId);
            MapWaterBodyDoc[] oldBodies = oldBody is null ? Array.Empty<MapWaterBodyDoc>() : new[] { oldBody };
            MapWaterBodyDoc[] newBodies = newBody is null ? Array.Empty<MapWaterBodyDoc>() : new[] { newBody };
            var change = new MapDigestChange("water:" + bodyId,
                oldBody is null ? null : MapWaterSet.BodyDigest(oldBody), newBody is null ? null : MapWaterSet.BodyDigest(newBody));
            MapNativeInvalidation flags = BodyInvalidation;
            if (oldBody is null || newBody is null || oldBody.RenderMaterial != newBody.RenderMaterial)
                flags |= MapNativeInvalidation.Material;
            return Effects(Bounds(oldBodies), Bounds(newBodies), oldBodies, newBodies, new[] { change }, flags);
        }
        string key = edit switch { MapUpsertWaterMedium upsert => upsert.Medium.Key, MapRemoveWaterMedium remove => remove.Key, _ => throw new InvalidOperationException() };
        MapWaterMediumDoc? oldMedium = before!.Media.Find(m => m.Key == key), newMedium = after!.Media.Find(m => m.Key == key);
        MapWaterBodyDoc[] oldUsers = before.Bodies.Where(b => b.Medium == key).ToArray(), newUsers = after.Bodies.Where(b => b.Medium == key).ToArray();
        var mediumChange = new MapDigestChange("water-medium:" + key,
            oldMedium is null ? null : MapWaterSet.MediumDigest(oldMedium), newMedium is null ? null : MapWaterSet.MediumDigest(newMedium));
        return Effects(Bounds(oldUsers), Bounds(newUsers), oldUsers, newUsers, new[] { mediumChange }, BodyInvalidation);
    }

    static MapNativeEditEffects Effects(MapBox3? oldBounds, MapBox3? newBounds, IReadOnlyList<MapWaterBodyDoc> oldBodies,
        IReadOnlyList<MapWaterBodyDoc> newBodies, IReadOnlyList<MapDigestChange> changes, MapNativeInvalidation flags)
    {
        IEnumerable<MapWaterBodyDoc> bodies = oldBodies.Concat(newBodies);
        return new(oldBounds, newBounds, Array.Empty<MapPatchKey>(), Ids(bodies.SelectMany(b => b.Covers).Select(c => c.Id)),
            Array.Empty<string>(), Array.AsReadOnly(changes.ToArray()), flags)
        {
            WaterBodyIds = Ids(bodies.Select(b => b.Id)),
        };
    }

    static IReadOnlyList<string> Ids(IEnumerable<string> ids) => Array.AsReadOnly(ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

    static MapBox3? Bounds(IEnumerable<MapWaterBodyDoc> bodies)
    {
        MapBox3? bounds = null;
        foreach (MapWaterBodyDoc body in bodies)
        {
            MapWaterDomain domain = MapWaterDomain.Compile(body.Domain);
            double y = MapExactValue.FromSingle(body.SurfaceY).ToDouble();
            var box = new MapBox3(domain.Min.X.ToDouble(), y, domain.Min.Z.ToDouble(), domain.Max.X.ToDouble(), y, domain.Max.Z.ToDouble());
            bounds = bounds is { } existing ? existing.Union(box) : box;
        }
        return bounds;
    }
}
