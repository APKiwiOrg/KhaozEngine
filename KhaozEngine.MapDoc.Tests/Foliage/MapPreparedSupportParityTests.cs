using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

/// <summary>The prepared support kernel against the released point query. Both run on the same acquisition, so any
/// difference is the kernel's. One prepared instance serves several requests to prove it holds no request state.</summary>
public sealed class MapPreparedSupportParityTests
{
    static MapSupportRequest Req(float x, float y, float z, float up, float down) => SupportFixtures.Request(x, y, z, up, down);

    [Fact]
    public void PreparedSupport_FullRequestMatchesReleasedSelection()
    {
        var seen = new HashSet<MapSupportStatus>();
        MapDocument stacked = SupportFixtures.EightStacked(), cave = CaveFixtures.RampChamberShaft();
        MapSupportRequest level1 = Req(0.5f, 10.2f, 0.5f, 0.5f, 1f);
        var cases = new List<(MapDocument Doc, MapSupportRequest Request, MapQueryLimits? Limits)>
        {
            (stacked, level1, null),                                                        // automatic occupied space
            (stacked, Req(0.5f, 4.9f, 0.5f, 1f, 5f), null),                                 // asymmetric down search
            (stacked, Req(0.5f, 1.0f, 0.5f, 20f, 1f), null),                                // asymmetric up search
            (stacked, Req(0.5f, 7.0f, 0.5f, 1f, 1f), null),                                 // inside the rock
            (stacked, Req(0.5f, 30.2f, 0.5f, 0.5f, 1f) with { SpaceId = "level-3" }, null),
            (stacked, level1 with { SurfaceId = "floor-1" }, null),
            (stacked, level1 with { SurfaceId = "floor-0" }, null),                         // the filter excludes the only floor in range
            (stacked, level1 with { MaxSlopeRadians = 0f }, null),
            (stacked, level1 with { MaxSlopeRadians = MathF.PI / 4 }, null),
            (stacked, Req(0.5f, 70.2f, 0.5f, 0.5f, 100f), new MapQueryLimits(MaxSupportIntersections: 2)),
            (cave, Req(3.5f, 3.0f, 5.5f, 0.5f, 30f), null),                                 // reached through the shaft link
            (cave, Req(3.5f, 9.5f, 2.5f, 0.5f, 1f), null),
            (cave, Req(2.5f, 4.2f, 6.5f, 1f, 1f) with { MaxSlopeRadians = 0f }, null),       // flat chamber floor at slope 0
            (cave, Req(2.5f, 7.5f, 3.5f, 1f, 1f), null),                                    // the sloped chamber floor
            (cave, Req(2.5f, 7.5f, 3.5f, 1f, 1f) with { MaxSlopeRadians = MathF.PI / 4 }, null),
            (cave, Req(2.5f, 7.5f, 3.5f, 1f, 1f) with { MaxSlopeRadians = 0f }, null),
            (cave, Req(1e-30f, 10.5f, 4.5f, 0.5f, 1f), null),                               // not representable
            (SupportFixtures.EqualSlabs(secondIsPaint: false), Req(0.5f, 0.2f, 0.5f, 0.5f, 1f), null),
            (SupportFixtures.LegacyFallback(), Req(1.5f, 5.0f, -0.5f, 0.5f, 10f), null),
            (CaveFixtures.WithoutOpening("shaft-top"), Req(3.5f, 5.0f, 5.5f, 0.5f, 1f), null),
            (stacked, Req(0.5f, 10.2f, 0.5f, -1f, 1f), null),                               // invalid interval
            (stacked, Req(float.NaN, 10.2f, 0.5f, 0.5f, 1f), null),                         // invalid point
            (stacked, level1 with { MaxSlopeRadians = 2f }, null),                          // invalid slope
        };
        MapDocument slabs = SupportFixtures.EqualSlabs(secondIsPaint: false), painted = SupportFixtures.EqualSlabs(secondIsPaint: true);
        MapDocument step = SupportFixtures.Step();
        cases.Add((slabs, Req(0.5f, 0.2f, 0.5f, 0.5f, 1f) with { CurrentSupport = SupportFixtures.FirstFace(slabs, "slab-a") }, null));
        cases.Add((painted, Req(0.5f, 0.2f, 0.5f, 0.5f, 1f) with { CurrentSupport = SupportFixtures.FirstFace(painted, "slab-a") }, null));
        cases.Add((step, Req(1.5f, 0.35f, 0.5f, 0.5f, 1f) with { CurrentSupport = SupportFixtures.FirstFace(step, "low") }, null));
        foreach (var (doc, request, limits) in cases)
        {
            // An invalid point cannot center a scope, so its acquisition is taken around the valid request.
            MapScopedSurfaces view = SupportFixtures.Acquire(doc, float.IsFinite(request.Point.Local.X) ? request.Point : level1.Point, limits);
            MapSupportResult released = new MapSupportQuery(view).Select(request);
            MapSupportResult prepared = MapSupportPreparation.Prepare(view).Sample(request);
            Same(released, prepared);
            seen.Add(prepared.Status);
        }

        // Outside the acquired scope: the view is acquired around a different point.
        MapScopedSurfaces elsewhere = SupportFixtures.Acquire(stacked, Req(40f, 10.2f, 40f, 0.5f, 1f).Point);
        Same(new MapSupportQuery(elsewhere).Select(level1), MapSupportPreparation.Prepare(elsewhere).Sample(level1));
        Assert.Equal(MapSupportStatus.MissingGeometry, MapSupportPreparation.Prepare(elsewhere).Sample(level1).Status);

        // The released named-owner and exact entry points reach the same kernel through the full request.
        MapScopedSurfaces stackedView = SupportFixtures.Acquire(stacked, level1.Point);
        MapPreparedSupport shared = MapSupportPreparation.Prepare(stackedView);
        var released1 = new MapSupportQuery(stackedView);
        foreach (string surface in new[] { "floor-1", "floor-0", "ceiling-1", "missing" })
        {
            MapSupportResult named = shared.Sample(new(level1.Point, surface, null, null, 0, 0, null), namedSurfaceOnly: true);
            Same(released1.SampleSurface(level1.Point, surface), named);
            seen.Add(named.Status);
        }
        var exact = new MapExactPoint(new(1, 2), MapExactValue.FromSingle(10.2f), new(1, 2));
        Same(released1.SelectExact(level1, exact), shared.Sample(level1, exactWorld: exact));
        Same(released1.SampleSurfaceExact(exact, "floor-1"),
            shared.Sample(new(level1.Point, "floor-1", null, null, 0, 0, null), namedSurfaceOnly: true, exactWorld: exact));

        // One prepared instance honours each request's own current support, slope and filters, never a remembered one.
        // The cell centre lies on the seam between the cell's two triangles, so the current support picks the face. On
        // the cave rim, two surfaces meet along an authored seam.
        MapFaceKey?[] diagonal = CaveFixtures.Compile(stacked, "floor-1").Faces.Where(f => f.Key.Primitive == 0)
            .Select(f => (MapFaceKey?)f.Key).ToArray();
        Assert.Equal(2, diagonal.Length);
        foreach (MapFaceKey? current in diagonal.Append(null))
            foreach (float? slope in new float?[] { null, 0f, MathF.PI / 4 })
                foreach (string? surface in new[] { null, "floor-1" })
                {
                    MapSupportRequest request = level1 with { CurrentSupport = current, MaxSlopeRadians = slope, SurfaceId = surface };
                    Same(released1.Select(request), shared.Sample(request));
                }
        Assert.Equal(diagonal[0], shared.Sample(level1 with { CurrentSupport = diagonal[0] }).Face);
        Assert.Equal(diagonal[1], shared.Sample(level1 with { CurrentSupport = diagonal[1] }).Face);
        MapSupportRequest rimRequest = Req(3.5f, 10.2f, 2f, 0.5f, 1f);
        MapScopedSurfaces rim = SupportFixtures.Acquire(cave, rimRequest.Point);
        MapPreparedSupport onRim = MapSupportPreparation.Prepare(rim);
        MapFaceKey?[] rimFaces = new[] { "outer", "cave-floor" }.SelectMany(id => CaveFixtures.Compile(cave, id).Faces
            .Where(f => f.Key.Primitive == 2 * 64 + 3 || f.Key.Primitive == 64 + 3).Select(f => (MapFaceKey?)f.Key)).Append(null).ToArray();
        Assert.True(rimFaces.Length > 2);
        foreach (MapFaceKey? current in rimFaces)
        {
            MapSupportRequest request = rimRequest with { CurrentSupport = current };
            Same(new MapSupportQuery(rim).Select(request), onRim.Sample(request));
        }

        Assert.Equal(Enum.GetValues<MapSupportStatus>().ToHashSet(), seen);
    }

    [Fact]
    public void SurfaceAndSpaceBindings_NormalizeToOneSupportSurface()
    {
        // Nested rooms: the parent room's floor under the inner room's footprint.
        MapEffectiveResources nested = PrefabFixtures.Effective(PrefabFixtures.Cave("nested"));
        MapScopedSurfaces view = nested.CompleteView();
        MapPreparedSupport prepared = MapSupportPreparation.Prepare(view);
        MapExactPoint probe = PrefabFixtures.RoomProbe(nested, 2, 0, -2);
        var xz = new MapExactXz(probe.X, probe.Z);
        MapSupportResult bySurface = prepared.SampleBinding(Surface("a/floor"), xz);
        MapSupportResult bySpace = prepared.SampleBinding(Space("a/room", 0.5f, 1, 1), xz);
        Assert.Equal(MapSupportStatus.Supported, bySurface.Status);
        Assert.Equal(MapSupportStatus.Supported, bySpace.Status);
        Assert.Equal(bySurface.Face, bySpace.Face);
        Assert.Equal("a/floor", bySpace.Face!.Value.OwnerId);
        Assert.Equal("a/room", bySurface.SpaceId);
        Assert.Equal("a/room", bySpace.SpaceId);
        MapSupportResult inner = prepared.SampleBinding(Space("prefab/a/Record/inner-room", 1.5f, 2, 0.25f), xz);
        Assert.NotEqual(MapSupportStatus.Supported, inner.Status);                            // the inner room never borrows its parent's floor
        foreach (MapSupportBinding binding in new[] { Surface("a/floor"), Space("a/room", 0.5f, 1, 1), Space("prefab/a/Record/inner-room", 1.5f, 2, 0.25f) })
        {
            MapSupportResult seat = MapResolverV2.SeatSupport(probe, binding, nested.SurfaceSource, new[] { nested.Digest }, new());
            MapSupportResult sampled = prepared.SampleBinding(binding, xz);
            Assert.Equal((seat.Status, seat.Face, BitConverter.SingleToInt32Bits(seat.WorldY), seat.SpaceId),
                (sampled.Status, sampled.Face, BitConverter.SingleToInt32Bits(sampled.WorldY), sampled.SpaceId));
        }

        // A surface and a space record both spelled "shared" stay in their own namespaces.
        MapDocument doc = SharedNamespaces();
        MapScopedSurfaces whole = MapScopedSurfaces.CompleteView(doc.Surfaces);
        MapPreparedSupport sharedPrepared = MapSupportPreparation.Prepare(whole);
        var west = new MapExactXz(new(1, 2), new(1, 2));
        MapSupportResult spaceShared = sharedPrepared.SampleBinding(Space("shared", 0.2f, 1, 1), west);
        MapSupportResult surfaceBase = sharedPrepared.SampleBinding(Surface("base"), west);
        Assert.Equal(MapSupportStatus.Supported, spaceShared.Status);
        Assert.Equal(surfaceBase.Face, spaceShared.Face);
        Assert.Equal("base", spaceShared.Face!.Value.OwnerId);
        Assert.Equal("shared", spaceShared.SpaceId);
        Assert.Equal(MapSupportStatus.NoSupport, sharedPrepared.SampleBinding(Surface("shared"), west).Status);
        MapSupportResult east = sharedPrepared.SampleBinding(Surface("shared"), new(new(9, 2), new(1, 2)));
        Assert.Equal(("shared", "elsewhere"), (east.Face!.Value.OwnerId, east.SpaceId));
    }

    static MapSupportBinding Surface(string id) => new(MapSupportBindingKind.Surface, id, null, null, null, null);
    static MapSupportBinding Space(string id, float y, float below, float above) => new(MapSupportBindingKind.Space, null, id, y, below, above);

    // Floor "base" over cells x 0 to 2 carries space "shared". Floor "shared" over cells x 4 to 6, 3 m higher, carries
    // space "elsewhere". Text IDs alone would confuse the two.
    static MapDocument SharedNamespaces()
    {
        var set = new MapSurfaceSet();
        CaveFixtures.AddSurface(set, "base", MapSurfaceRole.SupportFloor, 0, 0, 2, 1, (_, _) => 0);
        CaveFixtures.AddSurface(set, "shared", MapSurfaceRole.SupportFloor, 4, 0, 2, 1, (_, _) => 300);
        MapSurfacePatch basePatch = CaveFixtures.Patch(set, "base"), sharedPatch = CaveFixtures.Patch(set, "shared");
        basePatch.Records.Add(new MapSpaceDoc("shared", MapSpaceKind.Exterior, null, null, [], [], [], []));
        basePatch.Records.Add(new MapSpaceFootprint("shared-cells", new("shared", basePatch.Key), basePatch.Key,
            new[] { 0, 1 }, CaveFixtures.Floor("base"), CaveFixtures.OpenTop));
        sharedPatch.Records.Add(new MapSpaceDoc("elsewhere", MapSpaceKind.Exterior, null, null, [], [], [], []));
        sharedPatch.Records.Add(new MapSpaceFootprint("elsewhere-cells", new("elsewhere", sharedPatch.Key), sharedPatch.Key,
            new[] { 4, 5 }, CaveFixtures.Floor("shared"), CaveFixtures.OpenTop));
        return CaveFixtures.Document(set);
    }

    static void Same(MapSupportResult released, MapSupportResult prepared)
    {
        Assert.Equal(released.Status, prepared.Status);
        Assert.Equal(released.Face, prepared.Face);
        Assert.Equal(BitConverter.SingleToInt32Bits(released.WorldY), BitConverter.SingleToInt32Bits(prepared.WorldY));
        Assert.Equal(released.Normal, prepared.Normal);
        Assert.Equal(released.SpaceId, prepared.SpaceId);
        Assert.Equal(released.Via, prepared.Via);
        Assert.Equal(released.Compatibility, prepared.Compatibility);
        Assert.Equal(released.IsCaptureSupport, prepared.IsCaptureSupport);
        Assert.Equal(released.Detail, prepared.Detail);
        Assert.Equal((released.Witness.SnapshotId, released.Witness.ScopedDigest, released.Witness.Frame,
            released.Witness.QueryPolicyVersion, released.Witness.Complete), (prepared.Witness.SnapshotId,
            prepared.Witness.ScopedDigest, prepared.Witness.Frame, prepared.Witness.QueryPolicyVersion, prepared.Witness.Complete));
    }
}
