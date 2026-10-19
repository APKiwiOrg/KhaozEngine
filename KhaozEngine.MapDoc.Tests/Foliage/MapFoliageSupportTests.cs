using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

/// <summary>Support ownership of prepared foliage samples: bindings normalize to the selected support surface, named
/// support never falls through to another floor, typed refusals stay refusals and candidates never query support.</summary>
public sealed class MapFoliageSupportTests
{
    static readonly MapFoliageLimits Limits = new();
    static readonly MapFoliageChunkKey Origin = new(0, 0);

    static MapSupportBinding Space(string space, float referenceY, float below, float above) =>
        new(MapSupportBindingKind.Space, null, space, referenceY, below, above);

    static MapPreparedFoliageSupport Prepared(FoliageFixture fixture, MapFoliageChunkKey? chunk = null)
    {
        MapFoliageGenerationContext context = FoliageFixtures.Context(fixture);
        return MapFoliageSupport.Prepare(context, context.Foliage!.Layers[0], chunk ?? Origin, Limits);
    }

    [Fact]
    public void NamedSurfaceAndSpaceBindings_RefuseWithoutFallingThrough()
    {
        // The same floor under a Surface binding and a Space binding has one canonical support owner. The occupied space
        // is a separate fact.
        MapFoliageSurfacePoint surfacePoint = Prepared(FoliageFixtures.Stacked(FoliageFixtures.Surface("floor-1"))).Sample(0.5f, 0.5f);
        MapFoliageSurfacePoint spacePoint = Prepared(FoliageFixtures.Stacked(Space("level-1", 10.2f, 1f, 0.5f))).Sample(0.5f, 0.5f);
        Assert.Equal((MapFoliageStatus.Complete, MapFoliageReason.Accepted), (surfacePoint.Status, surfacePoint.Reason));
        Assert.Equal((MapFoliageStatus.Complete, MapFoliageReason.Accepted), (spacePoint.Status, spacePoint.Reason));
        Assert.Equal(surfacePoint.SupportOwnerId, spacePoint.SupportOwnerId);
        Assert.Equal("floor-1", surfacePoint.SupportOwnerId);
        Assert.Equal("level-1", spacePoint.OccupiedSpaceId);
        Assert.Equal("level-1", surfacePoint.OccupiedSpaceId);
        Assert.Equal(surfacePoint.Face, spacePoint.Face);
        Assert.Equal(10f, surfacePoint.Height);
        Assert.Equal(surfacePoint.Height, spacePoint.Height);

        // No eligible support inside the bound space refuses, although floor-0 and floor-2 remain in the column.
        MapPreparedFoliageSupport stranded = Prepared(FoliageFixtures.Stacked(Space("level-1", 15f, 0.5f, 0.5f)));
        Assert.Equal(MapFoliageStatus.Complete, stranded.Status);
        MapFoliageSurfacePoint missing = stranded.Sample(0.5f, 0.5f);
        Assert.Equal((MapFoliageStatus.MissingSupport, MapFoliageReason.Refused), (missing.Status, missing.Reason));
        Assert.Null(missing.SupportOwnerId);

        // A named surface that is not an effective support floor refuses the whole preparation.
        FoliageFixture stack = FoliageFixtures.Stacked(FoliageFixtures.Surface("floor-1"));
        MapFoliageGenerationContext context = FoliageFixtures.Context(stack);
        MapFoliageLayer layer = context.Foliage!.Layers[0];
        IReadOnlyList<MapSurfaceRef> declared = context.Resources.SurfaceSource.Surfaces;
        MapSurfaceScope scope = MapFoliageSupport.Scope(layer, Origin, declared, context.Resources.Transforms);
        MapPreparedFoliageSupport withoutFloor = MapFoliageSupport.Prepare(context.Acquire(scope), context.PaintFor(scope),
            declared.Where(s => s.Id != "floor-1").ToArray(), layer, Origin, Limits);
        Assert.Equal(MapFoliageStatus.MissingSupport, withoutFloor.Status);
        Assert.Equal(MapFoliageStatus.MissingSupport, withoutFloor.Sample(0.5f, 0.5f).Status);

        // A complete known hole yields no sample under either binding: no face, no density, no borrowed floor.
        MapFoliageSurfacePoint knownOutside = Prepared(stack).Sample(3.5f, 0.5f);
        Assert.Equal(MapFoliageStatus.Complete, knownOutside.Status);
        Assert.Equal(MapFoliageReason.OutsideDomain, knownOutside.Reason);
        Assert.Equal(("floor-1", (MapFaceKey?)null, 0f), (knownOutside.SupportOwnerId, knownOutside.Face, knownOutside.Density));
        MapFoliageSurfacePoint spaceOutside = Prepared(FoliageFixtures.Stacked(Space("level-1", 10.2f, 1f, 0.5f))).Sample(3.5f, 0.5f);
        Assert.Equal((MapFoliageStatus.Complete, MapFoliageReason.OutsideDomain, (string?)null),
            (spaceOutside.Status, spaceOutside.Reason, spaceOutside.SupportOwnerId));

        // Advertised but unavailable data refuses, and equal-height independent owners are ambiguous.
        MapFoliageGenerationContext halo = FoliageFixtures.UnavailableContext(FoliageFixtures.UnavailableHalo());
        MapFoliageSurfacePoint unavailable = MapFoliageSupport.Prepare(halo, halo.Foliage!.Layers[0], new(1, 0), Limits).Sample(63.5f, -32f);
        Assert.Equal(MapFoliageStatus.Unavailable, unavailable.Status);
        FoliageFixture slabs = FoliageFixtures.Slabs(Space("room", 0.2f, 1f, 0.5f));
        MapFoliageSurfacePoint ambiguous = Prepared(slabs).Sample(0.5f, 0.5f);
        Assert.Equal((MapFoliageStatus.Ambiguous, MapFoliageReason.Refused, (MapFaceKey?)null), (ambiguous.Status, ambiguous.Reason, ambiguous.Face));
        Assert.Equal(MapFoliageReason.Refused, unavailable.Reason);
    }

    [Fact]
    public void SpaceBinding_DecidesEveryCandidateLikeAFreshSelection()
    {
        // The released hut, turned so its room prism's edges cross the world lattice diagonally. Inside the room a
        // 1 m ramp rises through the search band [0.5, 1.5] mid-cell and leaves it at x = 14 inside the room, and a 1.5 m
        // terrace on an offset lattice crosses the ramp's height at x = 12, so the higher floor changes per point, never per
        // cell or by floor ID.
        var counts = CompareRoom(RoomSpans("basic"), Space("a/room", 1f, 0.5f, 0.5f), "a/room");
        Assert.All(new[] { "ramp", "terrace", "missing", "outside" }, c => Assert.True(counts.GetValueOrDefault(c) > 0, c));

        // The parent occupies [-2, 2) under a 2 m lid while the room prism spans [0, 3): a loft face at 2.5 m inside the
        // prism lies outside the room's released occupied part, so the band [2, 3] finds no room support there.
        counts = CompareRoom(RoomSpans("clipped"), Space("a/room", 2.5f, 0.5f, 0.5f), "a/room");
        Assert.All(new[] { "missing", "outside" }, c => Assert.True(counts.GetValueOrDefault(c) > 0, c));
        Assert.Equal(0, counts.GetValueOrDefault("loft"));

        // A Surface binding reads its occupied space at each candidate's face height, so the room edge through a ramp cell
        // splits that cell's occupants.
        counts = CompareRoom(RoomSpans("basic"), FoliageFixtures.Surface("ramp"), null);
        Assert.All(new[] { "a/room", "exterior" }, c => Assert.True(counts.GetValueOrDefault("space:" + c) > 0, c));

        // A bound space absent from the chunk's acquisition refuses rather than reporting a known outside.
        MapEffectiveResources resources = RoomSpans("basic");
        MapFoliageLayer absent = RoomLayer(Space("nowhere", 1f, 0.5f, 0.5f));
        MapSurfaceScope scope = MapFoliageSupport.Scope(absent, Origin, resources.SurfaceSource.Surfaces, resources.Transforms);
        MapPreparedFoliageSupport refused = MapFoliageSupport.Prepare(resources.Acquire(scope), resources.PaintRegions,
            resources.SurfaceSource.Surfaces, absent, Origin, Limits);
        Assert.Equal(MapFoliageStatus.Unavailable, refused.Status);
        Assert.Equal((MapFoliageStatus.Unavailable, MapFoliageReason.Refused), (refused.Sample(10f, -8f).Status, refused.Sample(10f, -8f).Reason));
    }

    [Fact]
    public void SpaceBinding_PortalsMissingBoundsAndLegacyFallbackFollowReleasedMembership()
    {
        // Candidates exactly on the released cave mouth, an incident non-room portal band between chamber and outside.
        var onMouth = Enumerable.Range(0, 17).Select(i => (2f + i * 0.25f, 3f, false));
        var around = Grid(1.513f, 6.6f, 1.513f, 8.6f, 0.211f);
        foreach (float reference in new[] { 8.2f, 9.5f })
        {
            var counts = CompareContext(FoliageFixtures.Wrapped(CaveFixtures.RampChamberShaft().Surfaces, Space("chamber", reference, 0.5f, 0.5f), -1, -1, 6, 6),
                "chamber", onMouth.Concat(around));
            Assert.True(counts.Values.Sum() > 0);
        }

        // A 0.5 m lid with one known missing cell under a quarter of base cell (1, 1): only candidates under that quarter
        // refuse, through the point's own bound, and the rest of the same support cell is supported.
        var pocket = CompareContext(FoliageFixtures.Wrapped(FoliageFixtures.Pocket(), Space("pocket", 0.2f, 0.5f, 0.5f), -1, -1, 4, 4),
            "pocket", Grid(0.013f, 4f, 0.013f, 4f, 0.091f));
        Assert.True(pocket.GetValueOrDefault("MissingGeometry") > 0);
        Assert.True(pocket.GetValueOrDefault("base") > 0);

        // The released tagged legacy row: a reachable fallback at 3 m from ReferenceY 5, and below its floor from 2.
        MapSurfaceSet row = SupportFixtures.LegacyFallback().Surfaces;
        var reachable = CompareContext(FoliageFixtures.Wrapped(row, Space("world", 5f, 10f, 0.5f), -1, -3, 4, 4), "world",
            Grid(0.013f, 4f, -0.987f, 0f, 0.097f));
        Assert.True(reachable.GetValueOrDefault("fallback") > 0);
        var below = CompareContext(FoliageFixtures.Wrapped(row, Space("world", 2f, 0f, 2f), -1, -3, 4, 4), "world",
            Grid(0.013f, 4f, -0.987f, 0f, 0.097f).Append((1.5f, -0.5f, false)));
        Assert.Equal(0, below.GetValueOrDefault("fallback"));
        Assert.True(below.GetValueOrDefault("missing") + below.GetValueOrDefault("outside") > 0);
    }

    static IEnumerable<(float X, float Z, bool Face)> Grid(float x0, float x1, float z0, float z1, float step)
    {
        for (float z = z0; z < z1; z += step)
            for (float x = x0; x < x1; x += step) yield return (x, z, true);
    }

    static MapFoliageLayer RoomLayer(MapSupportBinding binding) => FoliageFixtures.Layer("room-cover", binding, 0, -16, 2, 9, 9,
        Enumerable.Repeat((byte)200, 81).ToArray(), []);

    static Dictionary<string, int> CompareRoom(MapEffectiveResources resources, MapSupportBinding binding, string? owner)
    {
        MapFoliageLayer layer = RoomLayer(binding);
        IReadOnlyList<MapSurfaceRef> declared = resources.SurfaceSource.Surfaces;
        MapSurfaceScope scope = MapFoliageSupport.Scope(layer, Origin, declared, resources.Transforms);
        MapScopedSurfaces view = resources.Acquire(scope);
        MapPreparedFoliageSupport prepared = MapFoliageSupport.Prepare(view, resources.PaintRegions, declared, layer, Origin, Limits);
        return Compare(view, prepared, binding, owner, Grid(8.013f, 16f, -12.987f, -3f, 0.173f));
    }

    static Dictionary<string, int> CompareContext(FoliageFixture fixture, string owner, IEnumerable<(float X, float Z, bool Face)> points)
    {
        MapFoliageGenerationContext context = FoliageFixtures.Context(fixture);
        MapFoliageLayer layer = context.Foliage!.Layers[0];
        MapPreparedFoliageSupport prepared = MapFoliageSupport.Prepare(context, layer, Origin, Limits);
        MapScopedSurfaces view = context.Acquire(MapFoliageSupport.Scope(layer, Origin, context.Resources.SurfaceSource.Surfaces,
            context.Resources.Transforms));
        return Compare(view, prepared, layer.Support, owner, points);
    }

    // Every candidate against a fresh per-point released selection on the same acquisition. A Space NoSupport is
    // MissingSupport exactly when the released membership column holds a part of the bound space at that XZ, else it is
    // OutsideDomain. Faces are compared off the lattice's triangle edges, where tie order cannot differ.
    static Dictionary<string, int> Compare(MapScopedSurfaces view, MapPreparedFoliageSupport prepared, MapSupportBinding binding, string? owner,
        IEnumerable<(float X, float Z, bool Face)> candidates)
    {
        Assert.Equal(MapFoliageStatus.Complete, prepared.Status);
        var points = candidates.ToArray();
        long before = MapPreparedSupport.ThreadSamples;
        MapFoliageSurfacePoint[] samples = points.Select(p => prepared.Sample(p.X, p.Z)).ToArray();
        Assert.Equal(0, MapPreparedSupport.ThreadSamples - before);
        MapPreparedSupport fresh = MapSupportPreparation.Prepare(view);
        var membership = new MapSpaceMembership(view);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < points.Length; i++)
        {
            var (x, z, face) = points[i];
            var xz = new MapExactXz(MapExactValue.FromSingle(x), MapExactValue.FromSingle(z));
            MapSupportResult result = fresh.SampleBinding(binding, xz);
            MapFoliageSurfacePoint sample = samples[i];
            string at = FormattableString.Invariant($"({x}, {z})");
            string category;
            if (result.Status is MapSupportStatus.Supported or MapSupportStatus.LegacyFallback)
            {
                Assert.True((MapFoliageStatus.Complete, MapFoliageSupportIdentity.From(result), result.SpaceId) ==
                    (sample.Status, sample.SupportOwnerId, sample.OccupiedSpaceId), $"{at}: {result.Status} {result.SpaceId} against {sample.Status} {sample.SupportOwnerId} {sample.OccupiedSpaceId}");
                if (result.Status == MapSupportStatus.Supported && face) Assert.Equal(result.Face, sample.Face);
                Assert.NotEqual(MapFoliageReason.OutsideDomain, sample.Reason);
                category = result.Status == MapSupportStatus.LegacyFallback ? "fallback" : result.Face!.Value.OwnerId;
                if (binding.Kind == MapSupportBindingKind.Surface) category = "space:" + result.SpaceId;
            }
            else if (result.Status == MapSupportStatus.NoSupport && binding.Kind == MapSupportBindingKind.Space)
            {
                var world = new MapExactPoint(xz.X, MapExactValue.FromSingle(binding.ReferenceY!.Value), xz.Z);
                MapMembershipResult column = membership.Column(MapEffectiveGeometry.Presentation(view, world), out IReadOnlyList<MapSpaceBoundsPart> parts);
                (MapFoliageStatus, MapFoliageReason) expected = column.Status is not (MapMembershipStatus.Resolved or MapMembershipStatus.Outside)
                    ? (MapFoliageStatus.Unavailable, MapFoliageReason.Refused)
                    : parts.Any(p => p.Owner == owner) ? (MapFoliageStatus.MissingSupport, MapFoliageReason.Refused)
                    : (MapFoliageStatus.Complete, MapFoliageReason.OutsideDomain);
                Assert.True(expected == (sample.Status, sample.Reason), $"{at}: expected {expected}, got {sample.Status} {sample.Reason}");
                category = expected.Item1 == MapFoliageStatus.MissingSupport ? "missing" : "outside";
            }
            else
            {
                Assert.True(MapFoliageSupport.Map(result.Status) == sample.Status, $"{at}: {result.Status} {result.Detail} against {sample.Status}");
                category = result.Status.ToString();
            }
            counts[category] = counts.GetValueOrDefault(category) + 1;
        }
        return counts;
    }

    // The released basic hut at (10.5, 0, -6) turned by 0.371 radians, plus two invented world floors that pass through
    // its room: "ramp" on the 1 m lattice at 0.15 x - 0.6 metres and a flat "terrace" at 1.2 m on a 1.5 m lattice that ends
    // at x = 12. "clipped" also lowers the exterior's open top to a 2 m "lid" ceiling and adds a flat "loft" floor at 2.5 m.
    static MapEffectiveResources RoomSpans(string variant)
    {
        PrefabFixture f = PrefabFixtures.Create("basic");
        MapPrefabInstance instance = f.Document.Prefabs!.Instances.Single();
        f.Document.Prefabs = new(1, 1, new[] { instance with { Transform = new(new Vector3(10.5f, 0, -6f), 0.371f, 1) } }, []);
        AddFloor(f.Document, "ramp", new(1, 1), 16, 16, x => -60 + 15 * x);
        AddFloor(f.Document, "terrace", new(3, 2), 8, 11, _ => 120);
        if (variant == "clipped")
        {
            AddFloor(f.Document, "loft", new(1, 1), 16, 16, _ => 250);
            AddFloor(f.Document, "lid", new(1, 1), 16, 16, _ => 200, MapSurfaceRole.Ceiling);
            var key = new MapPatchKey("exterior", 0, 0);
            MapSurfacePatch exterior = f.Document.Surfaces.Patches[key];
            int index = exterior.Records.FindIndex(r => r.Id == "exterior-domain");
            exterior.Records[index] = ((MapSpaceFootprint)exterior.Records[index]) with { Upper = new(MapBoundKind.Ceiling, "lid", null) };
            int surface = f.Document.Surfaces.Refs.FindIndex(r => r.Id == "exterior");
            f.Document.Surfaces.Refs[surface] = f.Document.Surfaces.Refs[surface] with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(f.Document.Surfaces.Refs[surface],
                    new[] { new KeyValuePair<MapPatchKey, string>(key, MapSurfaceSemantics.PatchDigest(exterior)) }),
            };
        }
        return MapPrefabResolver.Expand(f.Document, f.Assets);
    }

    static void AddFloor(MapDocument document, string id, MapRational cell, int width, int depth, Func<int, int> height,
        MapSurfaceRole role = MapSurfaceRole.SupportFloor)
    {
        var patch = new MapSurfacePatch
        {
            Key = new(id, 0, 0),
            Width = width,
            Depth = depth,
            Heights = Enumerable.Range(0, (width + 1) * (depth + 1)).Select(i => height(i % (width + 1))).ToArray(),
            Cells = Enumerable.Repeat(new MapSurfaceCell(3, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto), width * depth).ToArray(),
            Presence = new ulong[(width * depth + 63) / 64],
        };
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++) patch.SetPresent(x, z, true);
        var surface = new MapSurfaceRef(id, new(cell, new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0),
            role, MapPresencePolicy.Native, null, null, "");
        document.Surfaces.Refs.Add(surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, new[] { new KeyValuePair<MapPatchKey, string>(patch.Key, MapSurfaceSemantics.PatchDigest(patch)) }),
        });
        document.Surfaces.Patches.Add(patch.Key, patch);
    }

    [Fact]
    public void PreparedSurface_DoesNotQuerySupportPerCandidate()
    {
        FoliageFixture stack = FoliageFixtures.Stacked(FoliageFixtures.Surface("floor-1"));
        MapFoliageGenerationContext context = FoliageFixtures.Context(stack);
        MapFoliageLayer layer = context.Foliage!.Layers[0];
        long before = MapPreparedSupport.ThreadSamples;
        MapPreparedFoliageSupport prepared = MapFoliageSupport.Prepare(context, layer, Origin, Limits);
        long preparation = MapPreparedSupport.ThreadSamples - before;
        Assert.Equal(MapFoliageStatus.Complete, prepared.Status);
        // Preparation runs no support selection: occupied space is resolved per candidate over the shared context, whose
        // faces are charged beside the floor's own eight.
        Assert.Equal(0, preparation);
        Assert.Equal(0, prepared.Work.SurfaceQueries);
        Assert.Equal(1, prepared.Work.PrepareScopes);
        Assert.True(prepared.Work.Faces > 8);

        var points = new List<(float X, float Z)>();
        for (int z = 0; z < 20; z++)
            for (int x = 0; x < 20; x++) points.Add((0.05f + x * 0.1f, 0.05f + z * 0.1f));
        long candidatesStart = MapPreparedSupport.ThreadSamples;
        MapFoliageSurfacePoint[] samples = points.Select(p => prepared.Sample(p.X, p.Z)).ToArray();
        var candidatePhaseWork = new MapFoliageWork(points.Count, samples.Length, 0, 0, 0, 0, MapPreparedSupport.ThreadSamples - candidatesStart, 0);
        Assert.Equal(0, candidatePhaseWork.SurfaceQueries);
        Assert.Equal(prepared.Work, MapFoliageSupport.Prepare(context, layer, Origin, Limits).Work);

        // The prepared answers agree with a fresh full support selection at every candidate.
        MapScopedSurfaces view = context.Acquire(MapFoliageSupport.Scope(layer, Origin, context.Resources.SurfaceSource.Surfaces,
            context.Resources.Transforms));
        MapPreparedSupport fresh = MapSupportPreparation.Prepare(view);
        for (int i = 0; i < points.Count; i++)
        {
            MapSupportResult result = fresh.SampleBinding(layer.Support, new(MapExactValue.FromSingle(points[i].X), MapExactValue.FromSingle(points[i].Z)));
            Assert.Equal(MapSupportStatus.Supported, result.Status);
            Assert.Equal((MapFoliageSupportIdentity.From(result), result.SpaceId), (samples[i].SupportOwnerId, samples[i].OccupiedSpaceId));
            Assert.Equal(MapFoliageReason.Accepted, samples[i].Reason);
        }
    }
}
