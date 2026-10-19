using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
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
        Assert.Equal(MapFoliageStatus.MissingSupport, missing.Status);
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
        Assert.Equal(MapFoliageStatus.Ambiguous, ambiguous.Status);
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
