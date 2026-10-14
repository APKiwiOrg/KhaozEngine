using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>SF-R1 I1. The paint target cell filter refuses on distinct hit slots and costs one row mask per slot.</summary>
public sealed class MapPartialPaintCellTests
{
    static MapWideRational W(long numerator, long denominator) => MapWideRational.From(new MapExactValue(numerator, denominator));
    static MapWaterPolygons.Bounds Box(MapWideRational x0, MapWideRational z0, MapWideRational x1, MapWideRational z1) =>
        MapWaterPolygons.Bounds.Of(new MapWideXz(x0, z0), new MapWideXz(x1, z1));

    // One source covers a 100 by 100 slot target. The filter refuses at the first hit slot past the allowance
    // instead of marking the 40,960,000 cells underneath, and an allowance that admits every slot stays bounded
    // by one row operation per slot row.
    [Fact]
    public void OneLargeSource_OverManySlots_RefusesPastTheSlotAllowance()
    {
        var keys = Enumerable.Range(0, 100).SelectMany(z => Enumerable.Range(0, 100).Select(x => new MapPatchKey("ground", x, z))).ToArray();
        var cover = new[] { Box(W(1, 2), W(-12799, 2), W(12799, 2), W(-1, 2)) };

        Assert.Null(MapPartialPaintCompiler.CandidateCells(MapLatticeFrame.ImportedMetreCentimetre, cover, keys, 255));

        var all = MapPartialPaintCompiler.CandidateCells(MapLatticeFrame.ImportedMetreCentimetre, cover, keys, keys.Length);
        Assert.NotNull(all);
        Assert.Equal(keys, all.Select(p => p.Key));
        Assert.All(all, p => Assert.Equal(4096, p.Value.Count));
    }

    // Quarter metre cells, so a slot is 16 m wide. Sources straddle the seams at x 0 and x 16, cross a Z seam and
    // touch cells only along an edge. A cell is a candidate exactly when its rectangle openly overlaps a source.
    [Fact]
    public void Candidates_AreExactlyTheCellsOpenlyOverlappingASource()
    {
        var frame = MapLatticeFrame.ImportedMetreCentimetre with { CellUnitMetres = new(1, 4) };
        var keys = new[] { -2, -1, 0, 1 }.SelectMany(x => new[] { -1, 0, 1 }.Select(z => new MapPatchKey("ground", x, z))).ToArray();
        var sources = new[]
        {
            Box(W(-21, 10), W(-37, 10), W(13, 10), W(-3, 10)),
            Box(W(153, 10), W(-161, 10), W(169, 10), W(-158, 10)),
            Box(W(-20, 1), W(15, 1), W(-19, 1), W(17, 1)),
            Box(W(1, 4), W(-1, 2), W(3, 4), W(-1, 4)),
        };

        var actual = MapPartialPaintCompiler.CandidateCells(frame, sources, keys, keys.Length)!.ToDictionary(p => p.Key, p => p.Value);

        int candidates = 0;
        foreach (var key in keys)
            for (int cell = 0; cell < 4096; cell++)
            {
                long x = key.SlotX * 64 + cell % 64, z = key.SlotZ * 64 + cell / 64;
                var a = frame.WorldXz(MapLatticeAddress.Corner(x, z));
                var b = frame.WorldXz(MapLatticeAddress.Corner(x + 1, z + 1));
                var rect = MapWaterPolygons.Bounds.Of(MapWideXz.From(a), MapWideXz.From(b));
                bool expected = sources.Any(s => s.OverlapsOpen(rect));
                candidates += expected ? 1 : 0;
                Assert.Equal(expected, actual.TryGetValue(key, out var mask) && mask.Contains(cell));
            }
        Assert.True(candidates > 0);
        Assert.All(actual.Values, mask => Assert.True(mask.Count > 0));
    }
}
