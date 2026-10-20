using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;

namespace KhaozEngine.Tests.MapDoc.Foliage;

internal enum SupportParityMode { Select, Named, Exact, NamedExact, Binding }

/// <summary>One support request of the parity matrix with its own fresh acquisition. Binding cases also name the
/// released seat inputs.</summary>
internal sealed record SupportParityCase(string Name, MapScopedSurfaces View, SupportParityMode Mode, MapSupportRequest? Request,
    string? SurfaceId = null, MapExactPoint? Exact = null, MapSupportBinding? Binding = null, IMapSurfaceSource? Source = null,
    IReadOnlyList<string>? Assets = null);

/// <summary>The support parity matrix and its row format. This file uses released APIs only, so the same matrix runs
/// against a scratch extraction of the released code to capture its expected rows. Every call to <see cref="Matrix"/>
/// builds fresh documents and acquisitions, so no route sees another route's cached work.</summary>
internal static class SupportParityFixtures
{
    static MapSupportRequest Req(float x, float y, float z, float up, float down) => SupportFixtures.Request(x, y, z, up, down);

    internal static IReadOnlyList<SupportParityCase> Matrix()
    {
        var cases = new List<SupportParityCase>();
        MapDocument stacked = SupportFixtures.EightStacked(), cave = CaveFixtures.RampChamberShaft();
        MapSupportRequest level1 = Req(0.5f, 10.2f, 0.5f, 0.5f, 1f);
        void Select(string name, MapDocument doc, MapSupportRequest request, MapQueryLimits? limits = null, MapFramePoint? around = null) =>
            cases.Add(new(name, SupportFixtures.Acquire(doc, around ?? request.Point, limits), SupportParityMode.Select, request));

        Select("auto-space", stacked, level1);
        Select("asymmetric-down", stacked, Req(0.5f, 4.9f, 0.5f, 1f, 5f));
        Select("asymmetric-up", stacked, Req(0.5f, 1.0f, 0.5f, 20f, 1f));
        Select("inside-rock", stacked, Req(0.5f, 7.0f, 0.5f, 1f, 1f));
        Select("explicit-space", stacked, Req(0.5f, 30.2f, 0.5f, 0.5f, 1f) with { SpaceId = "level-3" });
        Select("filter-floor-1", stacked, level1 with { SurfaceId = "floor-1" });
        Select("filter-floor-0", stacked, level1 with { SurfaceId = "floor-0" });
        Select("slope-0", stacked, level1 with { MaxSlopeRadians = 0f });
        Select("slope-quarter", stacked, level1 with { MaxSlopeRadians = MathF.PI / 4 });
        Select("intersection-capacity", stacked, Req(0.5f, 70.2f, 0.5f, 0.5f, 100f), new MapQueryLimits(MaxSupportIntersections: 2));
        Select("shaft-link", cave, Req(3.5f, 3.0f, 5.5f, 0.5f, 30f));
        Select("cave-floor", cave, Req(3.5f, 9.5f, 2.5f, 0.5f, 1f));
        Select("flat-slope-0", cave, Req(2.5f, 4.2f, 6.5f, 1f, 1f) with { MaxSlopeRadians = 0f });
        Select("ramp", cave, Req(2.5f, 7.5f, 3.5f, 1f, 1f));
        Select("ramp-slope-quarter", cave, Req(2.5f, 7.5f, 3.5f, 1f, 1f) with { MaxSlopeRadians = MathF.PI / 4 });
        Select("ramp-slope-0", cave, Req(2.5f, 7.5f, 3.5f, 1f, 1f) with { MaxSlopeRadians = 0f });
        Select("tiny-coordinate", cave, Req(1e-30f, 10.5f, 4.5f, 0.5f, 1f));
        Select("equal-height", SupportFixtures.EqualSlabs(secondIsPaint: false), Req(0.5f, 0.2f, 0.5f, 0.5f, 1f));
        Select("legacy-fallback", SupportFixtures.LegacyFallback(), Req(1.5f, 5.0f, -0.5f, 0.5f, 10f));
        Select("undeclared-hole", CaveFixtures.WithoutOpening("shaft-top"), Req(3.5f, 5.0f, 5.5f, 0.5f, 1f));
        Select("invalid-interval", stacked, Req(0.5f, 10.2f, 0.5f, -1f, 1f));
        Select("invalid-point", stacked, Req(float.NaN, 10.2f, 0.5f, 0.5f, 1f), around: level1.Point);
        Select("invalid-slope", stacked, level1 with { MaxSlopeRadians = 2f });
        Select("out-of-scope", stacked, level1, around: Req(40f, 10.2f, 40f, 0.5f, 1f).Point);
        MapDocument slabs = SupportFixtures.EqualSlabs(secondIsPaint: false), painted = SupportFixtures.EqualSlabs(secondIsPaint: true);
        MapDocument step = SupportFixtures.Step();
        Select("equal-height-current", slabs, Req(0.5f, 0.2f, 0.5f, 0.5f, 1f) with { CurrentSupport = SupportFixtures.FirstFace(slabs, "slab-a") });
        Select("paint-current", painted, Req(0.5f, 0.2f, 0.5f, 0.5f, 1f) with { CurrentSupport = SupportFixtures.FirstFace(painted, "slab-a") });
        Select("step-current", step, Req(1.5f, 0.35f, 0.5f, 0.5f, 1f) with { CurrentSupport = SupportFixtures.FirstFace(step, "low") });

        // The cell centre lies on the seam between its two triangles, so the current support picks the face.
        MapFaceKey?[] diagonal = CaveFixtures.Compile(stacked, "floor-1").Faces.Where(f => f.Key.Primitive == 0)
            .Select(f => (MapFaceKey?)f.Key).Append(null).ToArray();
        for (int d = 0; d < diagonal.Length; d++)
            foreach (float? slope in new float?[] { null, 0f, MathF.PI / 4 })
                foreach (string? surface in new[] { null, "floor-1" })
                    Select(FormattableString.Invariant($"diagonal-{d}-{slope?.ToString("R", CultureInfo.InvariantCulture) ?? "none"}-{surface ?? "any"}"),
                        stacked, level1 with { CurrentSupport = diagonal[d], MaxSlopeRadians = slope, SurfaceId = surface });
        // On the cave rim, two surfaces meet along an authored seam.
        MapSupportRequest rim = Req(3.5f, 10.2f, 2f, 0.5f, 1f);
        MapFaceKey?[] rimFaces = new[] { "outer", "cave-floor" }.SelectMany(id => CaveFixtures.Compile(cave, id).Faces
            .Where(f => f.Key.Primitive == 2 * 64 + 3 || f.Key.Primitive == 64 + 3).Select(f => (MapFaceKey?)f.Key)).Append(null).ToArray();
        for (int r = 0; r < rimFaces.Length; r++)
            Select(FormattableString.Invariant($"rim-{r}"), cave, rim with { CurrentSupport = rimFaces[r] });

        foreach (string surface in new[] { "floor-1", "floor-0", "ceiling-1", "missing" })
            cases.Add(new("named-" + surface, SupportFixtures.Acquire(stacked, level1.Point), SupportParityMode.Named, level1, surface));
        var exact = new MapExactPoint(new(1, 2), MapExactValue.FromSingle(10.2f), new(1, 2));
        cases.Add(new("exact", SupportFixtures.Acquire(stacked, level1.Point), SupportParityMode.Exact, level1, Exact: exact));
        cases.Add(new("named-exact", SupportFixtures.Acquire(stacked, level1.Point), SupportParityMode.NamedExact, null, "floor-1", exact));

        // Bindings: the parent room's floor under a nested room, and a surface and a space both spelled "shared".
        MapEffectiveResources nested = PrefabFixtures.Effective(PrefabFixtures.Cave("nested"));
        MapExactPoint probe = PrefabFixtures.RoomProbe(nested, 2, 0, -2);
        foreach (var (name, binding) in new[] { ("nested-surface", Surface("a/floor")), ("nested-room", Space("a/room", 0.5f, 1, 1)),
            ("nested-inner", Space("prefab/a/Record/inner-room", 1.5f, 2, 0.25f)) })
            cases.Add(new(name, nested.CompleteView(), SupportParityMode.Binding, null, Exact: probe, Binding: binding,
                Source: nested.SurfaceSource, Assets: new[] { nested.Digest }));
        MapDocument shared = SharedNamespaces();
        var west = new MapExactPoint(new(1, 2), default, new(1, 2));
        var east = new MapExactPoint(new(9, 2), default, new(1, 2));
        foreach (var (name, binding, point) in new[] { ("shared-space", Space("shared", 0.2f, 1, 1), west), ("shared-base", Surface("base"), west),
            ("shared-surface-west", Surface("shared"), west), ("shared-surface-east", Surface("shared"), east) })
            cases.Add(new(name, MapScopedSurfaces.CompleteView(shared.Surfaces), SupportParityMode.Binding, null, Exact: point, Binding: binding,
                Source: MapDocumentSurfaceSource.Capture(shared), Assets: Array.Empty<string>()));
        return cases;
    }

    /// <summary>The released point-query route: <see cref="MapSupportQuery"/> for request cases and the shared seat
    /// query for bindings. Select cases also report counted point work from a second fresh query.</summary>
    internal static (MapSupportResult Result, MapBoundFaceWork? Work) Released(SupportParityCase c)
    {
        var query = new MapSupportQuery(c.View);
        switch (c.Mode)
        {
            case SupportParityMode.Select:
                MapSupportResult result = query.Select(c.Request!);
                var work = new MapBoundFaceWork();
                _ = new MapSupportQuery(c.View).Select(c.Request!, work);
                return (result, work);
            case SupportParityMode.Named: return (query.SampleSurface(c.Request!.Point, c.SurfaceId!), null);
            case SupportParityMode.Exact: return (query.SelectExact(c.Request!, c.Exact!.Value), null);
            case SupportParityMode.NamedExact: return (query.SampleSurfaceExact(c.Exact!.Value, c.SurfaceId!), null);
            default: return (MapResolverV2.SeatSupport(c.Exact!.Value, c.Binding!, c.Source!, c.Assets!, new()), null);
        }
    }

    /// <summary>One comparable row: status, exact face, height and normal bits, space, Via, compatibility, detail, the
    /// witness outcome and counted point work. A request route's witness must be its acquisition's read witness. A
    /// binding's released seat acquires its own scope, so only its completeness is compared.</summary>
    internal static string Row(SupportParityCase c, MapSupportResult r, MapBoundFaceWork? work)
    {
        string face = r.Face is { } f
            ? FormattableString.Invariant($"{f.OwnerId}@{Key(f.Patch)}#{f.Primitive}.{f.ParentTriangle}.{f.Child}.{f.Side}") : "-";
        string normal = r.Normal is { } n
            ? FormattableString.Invariant($"{Bits(n.X)},{Bits(n.Y)},{Bits(n.Z)}") : "-";
        string via = r.Via is { } v ? v.Id + "@" + Key(v.Anchor) : "-";
        string compat = r.Compatibility is { } t ? FormattableString.Invariant($"{Key(t.Patch)}#{t.SlotCell}#{t.Policy}") : "-";
        MapReadWitness w = r.Witness, own = c.View.ReadWitness;
        bool sameAsView = w.SnapshotId == own.SnapshotId && w.ScopedDigest == own.ScopedDigest && w.Frame == own.Frame &&
            w.QueryPolicyVersion == own.QueryPolicyVersion && w.Complete == own.Complete;
        string witness = c.Mode == SupportParityMode.Binding ? (w.Complete ? "complete" : "incomplete")
            : (w.Complete ? "complete" : "incomplete") + (sameAsView ? "/view" : "/other");
        string counted = work is null ? "-" : FormattableString.Invariant(
            $"{work.ContextsPrepared},{work.PatchValidations},{work.BoundFacesCounted},{work.ContextFacesCounted},{work.Compiles},{work.CompiledFaces}");
        return string.Join(" | ", c.Name, r.Status, face, Bits(r.WorldY).ToString(CultureInfo.InvariantCulture), normal,
            r.SpaceId ?? "-", via, compat, r.Detail ?? "-", witness, counted);
    }

    static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
    static string Key(MapPatchKey? key) => key is { } k ? FormattableString.Invariant($"{k.SurfaceId}:{k.SlotX}:{k.SlotZ}") : "-";

    internal static MapSupportBinding Surface(string id) => new(MapSupportBindingKind.Surface, id, null, null, null, null);
    internal static MapSupportBinding Space(string id, float y, float below, float above) => new(MapSupportBindingKind.Space, null, id, y, below, above);

    // Floor "base" over cells x 0 to 2 carries space "shared". Floor "shared" over cells x 4 to 6, 3 m higher, carries
    // space "elsewhere". Text IDs alone would confuse the two.
    internal static MapDocument SharedNamespaces()
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
}
