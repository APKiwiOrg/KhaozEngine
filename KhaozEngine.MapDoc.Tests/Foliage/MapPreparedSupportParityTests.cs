using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

/// <summary>Released-parity tests pin both public support routes to rows captured from the released point query at
/// 226a44bfb, before the selection kernel was shared. Adapter tests compare the two current routes with each other and
/// prove only that one prepared instance holds no request state, not released parity.</summary>
public sealed class MapPreparedSupportParityTests
{
    // Captured by running SupportParityFixtures.Matrix through SupportParityFixtures.Released against a scratch git
    // archive of 226a44bfb. Columns: case, status, face, WorldY bits, normal bits, space, Via, compatibility, detail,
    // witness outcome, counted point work (contexts, patch validations, bound faces, context faces, compiles, faces).
    static readonly string[] ReleasedRows =
    [
        "auto-space | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "asymmetric-down | Supported | floor-0@floor-0:0:0#0.0.0.Front | 0 | 0,1065353216,0 | level-0 | - | - | - | complete/view | 1,16,0,32,16,32",
        "asymmetric-up | Supported | floor-0@floor-0:0:0#0.0.0.Front | 0 | 0,1065353216,0 | level-0 | - | - | - | complete/view | 1,16,0,32,16,32",
        "inside-rock | NoSupport | - | 0 | - | - | - | - | outside every space | complete/view | 1,16,0,32,16,32",
        "explicit-space | Supported | floor-3@floor-3:0:0#0.0.0.Front | 1106247680 | 0,1065353216,0 | level-3 | - | - | - | complete/view | 1,16,0,32,16,32",
        "filter-floor-1 | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "filter-floor-0 | NoSupport | - | 0 | - | - | - | - | no eligible support in the selected space | complete/view | 1,16,0,32,16,32",
        "slope-0 | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "slope-quarter | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "intersection-capacity | CapacityExceeded | - | 0 | - | - | - | - | support intersections | complete/view | 1,16,0,32,16,32",
        "shaft-link | Supported | deep-floor@deep-floor:0:0#323.0.0.Front | -1046478848 | 0,1065353216,0 | deep | shaft-link@cave-floor:0:0 | - | - | complete/view | 1,5,0,10,5,10",
        "cave-floor | Supported | cave-floor@cave-floor:0:0#131.0.0.Front | 1091567616 | -2147483648,1055193390,1063581998 | outside | - | - | - | complete/view | 1,3,0,2,3,2",
        "flat-slope-0 | Supported | cave-floor@cave-floor:0:0#386.0.0.Front | 1082130432 | 0,1065353216,0 | chamber | - | - | - | complete/view | 1,5,0,10,5,10",
        "ramp | Supported | cave-floor@cave-floor:0:0#194.0.0.Front | 1088421888 | -2147483648,1055193390,1063581998 | chamber | - | - | - | complete/view | 1,4,0,6,4,6",
        "ramp-slope-quarter | NoSupport | - | 0 | - | - | - | - | no eligible support in the selected space | complete/view | 1,4,0,6,4,6",
        "ramp-slope-0 | NoSupport | - | 0 | - | - | - | - | no eligible support in the selected space | complete/view | 1,4,0,6,4,6",
        "tiny-coordinate | NotRepresentable | - | 0 | - | - | - | - | overflow | complete/view | 0,0,0,0,0,0",
        "equal-height | Ambiguous | - | 0 | - | - | - | - | equal-height independent support owners | complete/view | 1,2,0,4,2,4",
        "legacy-fallback | LegacyFallback | - | 1077936128 | - | world | - | plane-0:0:0#1#kemap/legacy-exterior/1 | - | complete/view | 1,1,0,0,1,0",
        "undeclared-hole | MissingGeometry | - | 0 | - | - | - | - | incomplete surface acquisition | incomplete/view | 0,0,0,0,0,0",
        "invalid-interval | Invalid | - | 0 | - | - | - | - | invalid point or support interval or slope | complete/view | 0,0,0,0,0,0",
        "invalid-point | Invalid | - | 0 | - | - | - | - | invalid point or support interval or slope | complete/view | 0,0,0,0,0,0",
        "invalid-slope | Invalid | - | 0 | - | - | - | - | invalid point or support interval or slope | complete/view | 0,0,0,0,0,0",
        "out-of-scope | MissingGeometry | - | 0 | - | - | - | - | outside acquired scope | complete/view | 0,0,0,0,0,0",
        "equal-height-current | Ambiguous | - | 0 | - | - | - | - | equal-height independent support owners | complete/view | 1,2,0,4,2,4",
        "paint-current | Supported | slab-a@slab-a:0:0#0.0.0.Front | 0 | 0,1065353216,0 | room | - | - | - | complete/view | 1,1,0,2,1,2",
        "step-current | Supported | step@step:0:0#1.0.0.Front | 1050253722 | 0,1065353216,0 | yard | - | - | - | complete/view | 1,2,0,2,2,2",
        "diagonal-0-none-any | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-0-none-floor-1 | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-0-0-any | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-0-0-floor-1 | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-0-0.7853982-any | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-0-0.7853982-floor-1 | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-1-none-any | Supported | floor-1@floor-1:0:0#0.1.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-1-none-floor-1 | Supported | floor-1@floor-1:0:0#0.1.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-1-0-any | Supported | floor-1@floor-1:0:0#0.1.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-1-0-floor-1 | Supported | floor-1@floor-1:0:0#0.1.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-1-0.7853982-any | Supported | floor-1@floor-1:0:0#0.1.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-1-0.7853982-floor-1 | Supported | floor-1@floor-1:0:0#0.1.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-2-none-any | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-2-none-floor-1 | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-2-0-any | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-2-0-floor-1 | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-2-0.7853982-any | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "diagonal-2-0.7853982-floor-1 | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | 1,16,0,32,16,32",
        "rim-0 | Supported | cave-floor@cave-floor:0:0#131.0.0.Front | 1092616192 | -2147483648,1055193390,1063581998 | outside | - | - | - | complete/view | 1,3,0,2,3,2",
        "rim-1 | Supported | cave-floor@cave-floor:0:0#131.0.0.Front | 1092616192 | -2147483648,1055193390,1063581998 | outside | - | - | - | complete/view | 1,3,0,2,3,2",
        "rim-2 | Supported | cave-floor@cave-floor:0:0#131.0.0.Front | 1092616192 | -2147483648,1055193390,1063581998 | outside | - | - | - | complete/view | 1,3,0,2,3,2",
        "rim-3 | Supported | cave-floor@cave-floor:0:0#131.0.0.Front | 1092616192 | -2147483648,1055193390,1063581998 | outside | - | - | - | complete/view | 1,3,0,2,3,2",
        "rim-4 | Supported | cave-floor@cave-floor:0:0#131.0.0.Front | 1092616192 | -2147483648,1055193390,1063581998 | outside | - | - | - | complete/view | 1,3,0,2,3,2",
        "named-floor-1 | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | -",
        "named-floor-0 | Supported | floor-0@floor-0:0:0#0.0.0.Front | 0 | 0,1065353216,0 | level-0 | - | - | - | complete/view | -",
        "named-ceiling-1 | NoSupport | - | 0 | - | - | - | - | surface has no physical support at XZ | complete/view | -",
        "named-missing | NoSupport | - | 0 | - | - | - | - | surface has no physical support at XZ | complete/view | -",
        "exact | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | -",
        "named-exact | Supported | floor-1@floor-1:0:0#0.0.0.Front | 1092616192 | 0,1065353216,0 | level-1 | - | - | - | complete/view | -",
        "nested-surface | Supported | a/floor@a/floor:0:0#130.0.0.Front | 0 | 0,1065353216,0 | a/room | - | - | - | complete | -",
        "nested-room | Supported | a/floor@a/floor:0:0#130.0.0.Front | 0 | 0,1065353216,0 | a/room | - | - | - | complete | -",
        "nested-inner | NoSupport | - | 0 | - | - | - | - | no eligible support in the selected space | complete | -",
        "shared-space | Supported | base@base:0:0#0.0.0.Front | 0 | 0,1065353216,0 | shared | - | - | - | complete | -",
        "shared-base | Supported | base@base:0:0#0.0.0.Front | 0 | 0,1065353216,0 | shared | - | - | - | complete | -",
        "shared-surface-west | NoSupport | - | 0 | - | - | - | - | surface has no physical support at XZ | complete | -",
        "shared-surface-east | Supported | shared@shared:0:0#4.0.0.Front | 1077936128 | 0,1065353216,0 | elsewhere | - | - | - | complete | -",
    ];

    [Fact]
    public void PreparedSupport_FullRequestMatchesReleasedSelection()
    {
        // Each route runs on its own fresh matrix, so neither sees the other's cached acquisition work.
        IReadOnlyList<SupportParityCase> query = SupportParityFixtures.Matrix(), prepared = SupportParityFixtures.Matrix();
        Assert.Equal(ReleasedRows, query.Select(c =>
        {
            var (result, work) = SupportParityFixtures.Released(c);
            return SupportParityFixtures.Row(c, result, work);
        }));
        Assert.Equal(ReleasedRows, prepared.Select(c =>
        {
            var (result, work) = Prepared(c);
            return SupportParityFixtures.Row(c, result, work);
        }));
        var statuses = ReleasedRows.Select(r => Enum.Parse<MapSupportStatus>(r.Split(" | ")[1])).ToHashSet();
        Assert.Equal(Enum.GetValues<MapSupportStatus>().ToHashSet(), statuses);
    }

    // The prepared route for every matrix mode, each Select case also counting work on a second fresh preparation.
    static (MapSupportResult Result, MapBoundFaceWork? Work) Prepared(SupportParityCase c)
    {
        MapPreparedSupport prepared = MapSupportPreparation.Prepare(c.View);
        switch (c.Mode)
        {
            case SupportParityMode.Select:
                MapSupportResult result = prepared.Sample(c.Request!);
                var work = new MapBoundFaceWork();
                _ = MapSupportPreparation.Prepare(c.View).Sample(c.Request!, work);
                return (result, work);
            case SupportParityMode.Named:
                return (prepared.Sample(new(c.Request!.Point, c.SurfaceId, null, null, 0, 0, null), namedSurfaceOnly: true), null);
            case SupportParityMode.Exact: return (prepared.Sample(c.Request!, exactWorld: c.Exact), null);
            case SupportParityMode.NamedExact:
                return (prepared.Sample(new(MapEffectiveGeometry.Presentation(c.View, c.Exact!.Value), c.SurfaceId, null, null, 0, 0, null),
                    namedSurfaceOnly: true, exactWorld: c.Exact), null);
            default: return (prepared.SampleBinding(c.Binding!, new(c.Exact!.Value.X, c.Exact.Value.Z)), null);
        }
    }

    /// <summary>Adapter test: one prepared instance answers every request of its acquisition exactly as a fresh point
    /// query does, honouring each request's own current support, slope and filter.</summary>
    [Fact]
    public void PreparedSupport_AdapterSharedInstanceHoldsNoRequestState()
    {
        MapDocument stacked = SupportFixtures.EightStacked();
        MapSupportRequest level1 = SupportFixtures.Request(0.5f, 10.2f, 0.5f, 0.5f, 1f);
        MapScopedSurfaces view = SupportFixtures.Acquire(stacked, level1.Point);
        MapPreparedSupport shared = MapSupportPreparation.Prepare(view);
        MapFaceKey?[] diagonal = CaveFixtures.Compile(stacked, "floor-1").Faces.Where(f => f.Key.Primitive == 0)
            .Select(f => (MapFaceKey?)f.Key).ToArray();
        Assert.Equal(2, diagonal.Length);
        for (int pass = 0; pass < 2; pass++)
            foreach (MapFaceKey? current in diagonal.Append(null))
                foreach (float? slope in new float?[] { null, 0f, MathF.PI / 4 })
                    foreach (string? surface in new[] { null, "floor-1", "floor-0" })
                    {
                        MapSupportRequest request = level1 with { CurrentSupport = current, MaxSlopeRadians = slope, SurfaceId = surface };
                        Same(new MapSupportQuery(view).Select(request), shared.Sample(request));
                    }
        Assert.Equal(diagonal[0], shared.Sample(level1 with { CurrentSupport = diagonal[0] }).Face);
        Assert.Equal(diagonal[1], shared.Sample(level1 with { CurrentSupport = diagonal[1] }).Face);
    }

    [Fact]
    public void SurfaceAndSpaceBindings_NormalizeToOneSupportSurface()
    {
        // Nested rooms: the parent room's floor under the inner room's footprint.
        MapEffectiveResources nested = PrefabFixtures.Effective(PrefabFixtures.Cave("nested"));
        MapPreparedSupport prepared = MapSupportPreparation.Prepare(nested.CompleteView());
        MapExactPoint probe = PrefabFixtures.RoomProbe(nested, 2, 0, -2);
        var xz = new MapExactXz(probe.X, probe.Z);
        MapSupportResult bySurface = prepared.SampleBinding(SupportParityFixtures.Surface("a/floor"), xz);
        MapSupportResult bySpace = prepared.SampleBinding(SupportParityFixtures.Space("a/room", 0.5f, 1, 1), xz);
        Assert.Equal(MapSupportStatus.Supported, bySurface.Status);
        Assert.Equal(MapSupportStatus.Supported, bySpace.Status);
        Assert.Equal(bySurface.Face, bySpace.Face);
        Assert.Equal("a/floor", bySpace.Face!.Value.OwnerId);
        Assert.Equal(("a/room", "a/room"), (bySurface.SpaceId, bySpace.SpaceId));
        MapSupportResult inner = prepared.SampleBinding(SupportParityFixtures.Space("prefab/a/Record/inner-room", 1.5f, 2, 0.25f), xz);
        Assert.Equal(MapSupportStatus.NoSupport, inner.Status);                                 // the inner room never borrows its parent's floor

        // A surface and a space record both spelled "shared" stay in their own namespaces.
        MapPreparedSupport shared = MapSupportPreparation.Prepare(MapScopedSurfaces.CompleteView(SupportParityFixtures.SharedNamespaces().Surfaces));
        var west = new MapExactXz(new(1, 2), new(1, 2));
        MapSupportResult spaceShared = shared.SampleBinding(SupportParityFixtures.Space("shared", 0.2f, 1, 1), west);
        MapSupportResult surfaceBase = shared.SampleBinding(SupportParityFixtures.Surface("base"), west);
        Assert.Equal(MapSupportStatus.Supported, spaceShared.Status);
        Assert.Equal(surfaceBase.Face, spaceShared.Face);
        Assert.Equal(("base", "shared"), (spaceShared.Face!.Value.OwnerId, spaceShared.SpaceId));
        Assert.Equal(MapSupportStatus.NoSupport, shared.SampleBinding(SupportParityFixtures.Surface("shared"), west).Status);
        MapSupportResult east = shared.SampleBinding(SupportParityFixtures.Surface("shared"), new(new(9, 2), new(1, 2)));
        Assert.Equal(("shared", "elsewhere"), (east.Face!.Value.OwnerId, east.SpaceId));
    }

    static void Same(MapSupportResult query, MapSupportResult prepared)
    {
        Assert.Equal(query.Status, prepared.Status);
        Assert.Equal(query.Face, prepared.Face);
        Assert.Equal(BitConverter.SingleToInt32Bits(query.WorldY), BitConverter.SingleToInt32Bits(prepared.WorldY));
        Assert.Equal(query.Normal, prepared.Normal);
        Assert.Equal((query.SpaceId, query.Via, query.Compatibility, query.Detail), (prepared.SpaceId, prepared.Via, prepared.Compatibility, prepared.Detail));
    }
}
