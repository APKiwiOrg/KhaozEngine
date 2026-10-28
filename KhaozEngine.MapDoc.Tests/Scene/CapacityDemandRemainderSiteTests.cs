using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc.Scene;

/// <summary>Accepted and refused rows for remainder and reachable-minimum gates. Each expectation comes from a test
/// ceiling, a fixture counter or a public result, never from the event under test.</summary>
[Collection("AllocSensitive")]
public sealed class CapacityDemandRemainderSiteTests
{
    static object[] At(CapacityDemandProbe probe, string site, string limit) =>
        probe.For(limit).Where(r => Read<string>(r, "Site") == site).ToArray();

    static (decimal Current, decimal Increment, decimal Allowance, bool Refused) Of(object row) =>
        (Read<decimal>(row, "CurrentDemand"), Read<decimal>(row, "ProposedIncrement"), Read<decimal>(row, "Allowance"), Read<bool>(row, "Refused"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointFaceRemainders_AcceptAndRefuseAfterThePrismVertices(bool refused)
    {
        var resolved = PrefabFixtures.Effective(PrefabFixtures.Create());
        var complete = resolved.CompleteView();
        var point = PrefabFixtures.Point(resolved, 2, 1, -2);
        MapScopedSurfaces Acquire(int faces) => resolved.Acquire(complete.Scope with { Limits = complete.Scope.Limits with { MaxInspectedFaces = faces } });
        // The point's own cold context counts its faces. The invented room's prism costs four footprint vertices first.
        var work = new MapBoundFaceWork();
        Assert.Equal(MapMembershipStatus.Resolved, new MapSpaceMembership(Acquire(4096)).Query(point, work).Status);
        long faces = work.ContextFacesCounted;
        Assert.True(faces > 1);
        int allowance = checked((int)(4 + faces - (refused ? 1 : 0)));
        var view = Acquire(allowance);
        // A supplied context holding every acquired cell, prepared without a face ceiling, so only the point's own
        // allowance can refuse.
        var cells = new System.Collections.Generic.List<MapCellDemand>();
        foreach (var row in view.Witness.Present)
            if (view.TryAcquiredPatch(row.Key, out var patch, out _) && patch is not null)
                for (int z = 0; z < patch.Depth; z++)
                    for (int x = 0; x < patch.Width; x++) cells.Add(new(row.Key, (z + patch.CellMinZ) * 64 + x + patch.CellMinX));
        var supplied = MapBoundFaceContext.Prepare(view, cells, 256, long.MaxValue, null, out _)!;
        var records = MapSpaceGeometry.Records(view).ToArray();
        int roots = records.OfType<MapSpaceFootprint>().Count();
        int prisms = records.OfType<MapPrismDomain>().Count();
        var scratch = new MapSpaceMembershipScratch(view, new MapMembershipPreparation(0, 0, 0, records.Length, roots, prisms, Math.Max(1, roots + prisms)));
        var membership = new MapSpaceMembership(view);
        using var probe = new CapacityDemandProbe();
        var pointBudget = membership.Query(point, supplied, Array.Empty<MapBoundFaceDemand>(), out _);
        var prepared = membership.Query(point, scratch);
        if (refused)
        {
            Assert.Equal(MapMembershipStatus.CapacityExceeded, pointBudget.Status);
            Assert.Equal(MapMembershipStatus.CapacityExceeded, prepared.Status);
        }
        foreach (string site in new[] { "MapSpaceMembership.MaxInspectedFaces.2", "MapSpaceMembershipScratch.MaxInspectedFaces.2" })
        {
            var rows = At(probe, site, "MaxInspectedFaces");
            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.Equal((4m, (decimal)allowance), (Read<decimal>(r, "CurrentDemand"), Read<decimal>(r, "Allowance"))));
            Assert.Equal((decimal)faces, Read<decimal>(rows[^1], "ProposedIncrement"));
            Assert.Equal(refused, Read<bool>(rows[^1], "Refused"));
            Assert.All(rows[..^1], r => Assert.False(Read<bool>(r, "Refused")));
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void WindowAttribution_PublishesTheReachableBindingWindow(bool innerBinds, bool refused)
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1000));
        // Each window already holds reused bytes. The binding window is the one with less remaining.
        using var outer = admission.LimitPreparation(innerBinds ? 40 : 20, "MaxPlanBytes", reused: 5);
        using var inner = admission.LimitPreparation(innerBinds ? 30 : 50, "MaxQueuedBytes", reused: 2);
        var (name, reused, remaining) = innerBinds ? ("MaxQueuedBytes", 2L, 30L) : ("MaxPlanBytes", 5L, 20L);
        long bytes = remaining + (refused ? 1 : 0);
        var attribution = new MapPreparationWindowAttribution(new object());
        using var probe = new CapacityDemandProbe();
        Action acquire = () => attribution.Acquire(bytes, admission.WindowIdentity)?.Dispose();
        if (refused) Assert.Throws<MapSceneCapacityException>(acquire); else acquire();
        var row = Assert.Single(probe.Events, r => Read<string>(r, "Site") == "MapPreparationWindowAttribution.Acquire");
        Assert.Equal(name, Read<string>(row, "LimitName"));
        Assert.Equal(((decimal)reused, (decimal)bytes, (decimal)(reused + remaining), refused), Of(row));
    }

    // A cave pocket whose 1 m floor covers the whole support read reach, so the cell gate admits the grid, bounded above
    // by a 4 by 4 lid at 3 m that only the shared context compiles.
    static FoliageFixture LidOverCoveredFloor()
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(MixedResolutionFixtures.Surface("base", 1, MapSurfaceRole.SupportFloor));
        set.Refs.Add(MixedResolutionFixtures.Surface("lid", 1, MapSurfaceRole.Ceiling));
        foreach (var (x, z) in new[] { (-1, -1), (0, -1), (-1, 0), (0, 0) })
        {
            var floor = MixedResolutionFixtures.Patch(new("base", x, z), 64, 64, (_, _) => 0);
            set.Patches.Add(floor.Key, floor);
        }
        var anchor = set.Patches[new("base", 0, 0)];
        anchor.Records.Add(new MapSpaceDoc("pocket", MapSpaceKind.Cave, null, null, [], [], [], []));
        anchor.Records.Add(new MapSpaceFootprint("pocket-cells", new("pocket", anchor.Key), anchor.Key,
            Enumerable.Range(0, 4).SelectMany(z => Enumerable.Range(0, 4).Select(x => z * MapPatchKey.SlotCells + x)).ToArray(),
            new(MapBoundKind.SupportFloor, "base", null), new(MapBoundKind.Ceiling, "lid", null)));
        var lid = MixedResolutionFixtures.Patch(new("lid", 0, 0), 4, 4, (_, _) => 300);
        set.Patches.Add(lid.Key, lid);
        return FoliageFixtures.Wrapped(set, new(MapSupportBindingKind.Space, null, "pocket", 0.2f, 0.5f, 0.5f), -1, -1, 4, 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FoliageSupportContext_RefusesCeilingFacesTheGridNeverCharged(bool refused)
    {
        var context = FoliageFixtures.Context(LidOverCoveredFloor());
        var layer = context.Foliage!.Layers[0];
        // The prepared work counts the floor's grid faces and the lid's context faces.
        var permissive = MapFoliageSupport.Prepare(context, layer, default, new());
        Assert.True(permissive.Status == MapFoliageStatus.Complete, $"{permissive.Status} {permissive.Detail} {permissive.Work}");
        long faces = permissive.Work.Faces;
        long ceiling = refused ? faces - 1 : faces;
        using var probe = new CapacityDemandProbe();
        var result = MapFoliageSupport.Prepare(context, layer, default, new(MaxFaces: (int)ceiling));
        Assert.True(result.Status == (refused ? MapFoliageStatus.CapacityExceeded : MapFoliageStatus.Complete),
            $"{result.Status} {result.Detail} {result.Work} permissive {permissive.Work}");
        // The grid charges only the floor, so the lid's faces first cross the ceiling in the shared context.
        var grid = At(probe, "MapFoliageSupportBuilder.MaxFaces.2", "MaxFaces");
        Assert.NotEmpty(grid);
        Assert.All(grid, r => Assert.False(Read<bool>(r, "Refused")));
        var rows = At(probe, "MapBoundFaceContext.MaxContextFaces.1", "MaxFaces");
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal((0m, (decimal)ceiling), (Read<decimal>(r, "CurrentDemand"), Read<decimal>(r, "Allowance"))));
        int first = Array.FindIndex(rows, r => Read<bool>(r, "Refused"));
        Assert.Equal(refused, first >= 0);
        // The context's face total is the prepared faces. A refusal is reached only by the context's last faces.
        Assert.Equal((0m, (decimal)faces, (decimal)ceiling, refused), Of(rows[refused ? first : ^1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExceptionalOwnerContext_RefusesFacesBeyondTheChunkSupport(bool refused)
    {
        var fixture = FoliageFixtures.Clearance("prefab");
        var context = FoliageFixtures.Context(fixture);
        var layer = context.Foliage!.Layers[0];
        // The chunk's support faces are the owner context's consumed baseline. The clearance work adds the owner's faces.
        long support = MapFoliageSupport.Prepare(context, layer, new(0, 0), new()).Work.Faces;
        long total = MapFoliageClearance.Prepare(context, layer, new(0, 0), new()).Work.Faces;
        Assert.True(total > support);
        long ceiling = refused ? total - 1 : total;
        using var probe = new CapacityDemandProbe();
        Assert.Equal(refused ? MapFoliageStatus.CapacityExceeded : MapFoliageStatus.Complete,
            MapFoliageClearance.Prepare(context, layer, new(0, 0), new(MaxFaces: (int)ceiling)).Status);
        var owner = At(probe, "MapBoundFaceContext.MaxContextFaces.1", "MaxFaces")
            .GroupBy(r => Read<long>(r, "AdmissionId")).Last().ToArray();
        Assert.All(owner, r => Assert.Equal(((decimal)support, (decimal)ceiling), (Read<decimal>(r, "CurrentDemand"), Read<decimal>(r, "Allowance"))));
        Assert.Equal(((decimal)support, (decimal)(total - support), (decimal)ceiling, refused), Of(owner[^1]));
        Assert.All(owner[..^1], r => Assert.False(Read<bool>(r, "Refused")));
    }
}
