using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
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
        MapEffectiveResources resources = RoomSpans();
        MapFoliageLayer layer = FoliageFixtures.Layer("room-cover", Space("a/room", 1f, 0.5f, 0.5f), 0, -16, 2, 9, 9,
            Enumerable.Repeat((byte)200, 81).ToArray(), []);
        IReadOnlyList<MapSurfaceRef> declared = resources.SurfaceSource.Surfaces;
        MapSurfaceScope scope = MapFoliageSupport.Scope(layer, Origin, declared, resources.Transforms);
        MapScopedSurfaces view = resources.Acquire(scope);
        MapPreparedFoliageSupport prepared = MapFoliageSupport.Prepare(view, resources.PaintRegions, declared, layer, Origin, Limits);
        Assert.Equal(MapFoliageStatus.Complete, prepared.Status);

        var points = new List<(float X, float Z)>();
        for (float z = -12.987f; z < -3f; z += 0.173f)
            for (float x = 8.013f; x < 16f; x += 0.173f) points.Add((x, z));
        long before = MapPreparedSupport.ThreadSamples;
        MapFoliageSurfacePoint[] samples = points.Select(p => prepared.Sample(p.X, p.Z)).ToArray();
        Assert.Equal(0, MapPreparedSupport.ThreadSamples - before);

        MapPreparedSupport fresh = MapSupportPreparation.Prepare(view);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < points.Count; i++)
        {
            MapSupportResult result = fresh.SampleBinding(layer.Support,
                new(MapExactValue.FromSingle(points[i].X), MapExactValue.FromSingle(points[i].Z)));
            MapFoliageSurfacePoint sample = samples[i];
            string category;
            if (result.Status == MapSupportStatus.Supported)
            {
                Assert.Equal((MapFoliageStatus.Complete, MapFoliageSupportIdentity.From(result), result.SpaceId, result.Face),
                    (sample.Status, sample.SupportOwnerId, sample.OccupiedSpaceId, sample.Face));
                Assert.NotEqual(MapFoliageReason.OutsideDomain, sample.Reason);
                category = result.Face!.Value.OwnerId;
            }
            else if (result.Status == MapSupportStatus.NoSupport)
            {
                Assert.True(sample.Status == MapFoliageStatus.MissingSupport || sample.Reason == MapFoliageReason.OutsideDomain,
                    $"({points[i].X}, {points[i].Z}): {sample.Status} {sample.Reason}");
                category = sample.Status == MapFoliageStatus.MissingSupport ? "missing" : "outside";
            }
            else
            {
                Assert.Equal(MapFoliageSupport.Map(result.Status), sample.Status);
                category = result.Status.ToString();
            }
            counts[category] = counts.GetValueOrDefault(category) + 1;
        }
        Assert.All(new[] { "ramp", "terrace", "missing", "outside" }, c => Assert.True(counts.GetValueOrDefault(c) > 0, c));
    }

    // The released basic hut at (10.5, 0, -6) turned by 0.371 radians, plus two invented world floors that pass through
    // its room: "ramp" on the 1 m lattice at 0.15 x - 0.6 metres and a flat "terrace" at 1.2 m on a 1.5 m lattice that ends
    // at x = 12.
    static MapEffectiveResources RoomSpans()
    {
        PrefabFixture f = PrefabFixtures.Create("basic");
        MapPrefabInstance instance = f.Document.Prefabs!.Instances.Single();
        f.Document.Prefabs = new(1, 1, new[] { instance with { Transform = new(new Vector3(10.5f, 0, -6f), 0.371f, 1) } }, []);
        AddFloor(f.Document, "ramp", new(1, 1), 16, 16, x => -60 + 15 * x);
        AddFloor(f.Document, "terrace", new(3, 2), 8, 11, _ => 120);
        return MapPrefabResolver.Expand(f.Document, f.Assets);
    }

    static void AddFloor(MapDocument document, string id, MapRational cell, int width, int depth, Func<int, int> height)
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
            MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, "");
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
        // One named selection per support cell centre of the two by two floor, all charged to preparation.
        Assert.Equal(4, preparation);
        Assert.Equal(4, prepared.Work.SurfaceQueries);
        Assert.Equal(1, prepared.Work.PrepareScopes);
        Assert.Equal(8, prepared.Work.Faces);

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
