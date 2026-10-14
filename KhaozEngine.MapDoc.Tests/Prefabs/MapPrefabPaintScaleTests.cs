using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

/// <summary>F-R12. Partial paint work is proportional to painted area, not to the whole target surface.</summary>
public sealed class MapPrefabPaintScaleTests
{
    // Five separated 2 by 2 m footprints on a flat floor of four regions, 64 by 32 one metre cells each.
    static readonly Vector3[] Scattered =
    {
        new(24.25f, 9, -16.125f), new(72.25f, 9, -16.125f), new(120.25f, 9, -16.125f),
        new(168.25f, 9, -16.125f), new(216.25f, 9, -16.125f),
    };

    [Fact]
    public void ScatteredPaint_OverSeveralRegions_CompilesAtDefaultBudgets()
    {
        var f = PrefabFixtures.ScatteredPaint(Scattered, 1, regions: 4, depth: 32, new(1, 1));
        var limits = new MapRefinementLimits();
        var ground = f.Document.Surfaces.Refs.Single(s => s.Id == "ground");
        long faces = f.Document.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == "ground")
            .Sum(p => (long)MapSurfaceCompiler.Compile(ground, p).Faces.Count);
        // Every footprint yields at least one paint source, so the old face by source product exceeds the pair budget.
        Assert.Equal(limits.MaxSourceFacesPerBound, faces);
        Assert.True(faces * Scattered.Length > limits.MaxPairChecks);

        var regions = PrefabFixtures.Effective(f).PaintRegions["ground"];

        string[] ids = Enumerable.Range(0, Scattered.Length).Select(i => (i == 0 ? "a" : "copy" + i) + "/paint").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(ids, regions.Regions.Select(r => r.PaintId).Distinct().Order(StringComparer.Ordinal));
        foreach (string id in ids)
            Assert.InRange(PrefabFixtures.PaintArea(new(regions.Regions.Where(r => r.PaintId == id).ToArray(), "")).ToDouble(), 3.999996d, 4.000004d);
        Assert.Equal(regions.Digest, PrefabFixtures.Effective(PrefabFixtures.ScatteredPaint(Scattered, 1, 4, 32, new(1, 1))).PaintRegions["ground"].Digest);
    }

    [Fact]
    public void DenseOverlappingPaint_StillRefusesItsPairBudget()
    {
        // Sixteen 14 by 14 m footprints over one 16 by 16 m region of quarter metre cells. Every candidate is real overlap.
        var sites = Enumerable.Range(0, 16).Select(i => new Vector3(1.25f + 0.25f * i, 9, -1.125f)).ToArray();
        var f = PrefabFixtures.ScatteredPaint(sites, 7, regions: 1, depth: 64, new(1, 4));

        var error = Assert.Throws<MapDocumentException>(() => PrefabFixtures.Effective(f));

        Assert.Contains("CapacityExceeded: paint pair checks", error.Message);
    }

    // Quarter metre cells from slot -1, so slots are 16 m wide. One footprint lies in the negative slot, one straddles
    // the seam at x 0 and one straddles the seam at x 16. The digest was recorded from the compiler before the broad
    // phase with a raised pair budget, and the default budget must publish the same regions.
    [Fact]
    public void SeamAndNegativeSlotPaint_KeepsItsRecordedRegions()
    {
        var sites = new Vector3[] { new(-8.25f, 9, -0.25f), new(-0.75f, 9, -0.25f), new(15.25f, 9, -0.25f) };
        var resources = PrefabFixtures.Effective(PrefabFixtures.ScatteredPaint(sites, 1, regions: 4, depth: 12, new(1, 4), firstSlot: -1));

        var raised = MapPartialPaintCompiler.Compile(resources.CompleteView(), "ground", new MapRefinementLimits(MaxPairChecks: 1 << 22));

        Assert.Equal(603, raised.Regions.Count);
        Assert.Equal("abe996c117a5d64e2ca289b912c3fe5d95265d0a75c25f8cc38b3a7a2464c37a", raised.Digest);
        Assert.Equal(raised.Digest, resources.PaintRegions["ground"].Digest);
        Assert.Equal(new[] { "a/paint", "copy1/paint", "copy2/paint" }, raised.Regions.Select(r => r.PaintId).Distinct().Order(StringComparer.Ordinal));
        Assert.Contains(raised.Regions, r => r.Face.Patch!.Value.SlotX == -1);
        Assert.Contains(raised.Regions, r => r.Face.Patch!.Value.SlotX == 1);
    }

    // Digests recorded from the compiler before the broad phase. Region order and every exact vertex are unchanged.
    [Theory]
    [InlineData("uneven-painted", "ground", 31, "5b4aadabc77aa4c9c199f9953c2cf462b93d39b9c7c22e5614634418259c3124")]
    [InlineData("paint-notch", "ground", 36, "14fbf1a2a53228c649272144d4daa4facfd96826320ab4604b6094ae7e971a13")]
    [InlineData("paint-hole", "ground", 76, "efa3532905be02cfa7bc7502bb9aea713d5eac4472cd53c30d784e058f801e7a")]
    [InlineData("prioritized-paint", "ground", 16, "9c1e7dedb387da6ebd92a200a4bb698ae4a61651c0c023709c834a67c53ced35")]
    [InlineData("identical-paint", "ground", 16, "c3d8360e0f4f663598b740a1634a2e40c7f29b9cae4179bd0ee028a552df3a31")]
    [InlineData("village", "exterior", 26, "927fa5a898e0a01b0f750b1cd04148c41cc1ad9ba282cfc223786f3a8fd9aca7")]
    public void ExistingPaintFixtures_KeepTheirPublishedRegions(string variant, string target, int count, string digest)
    {
        var fixture = variant == "village" ? PrefabFixtures.Village() : PrefabFixtures.Create(variant);

        var regions = PrefabFixtures.Effective(fixture).PaintRegions[target];

        Assert.Equal(count, regions.Regions.Count);
        Assert.Equal(digest, regions.Digest);
    }
}
