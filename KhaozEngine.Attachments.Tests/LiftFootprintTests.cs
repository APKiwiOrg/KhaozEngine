using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class LiftFootprintTests
{
    internal const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    const int MaxBodyTriangles = int.MaxValue / 64;

    [Fact]
    public void Region_heights_use_fixed_entries_on_flattened_body_triangles()
    {
        BodyRegionMap body = new("rig-v1:" + Digest, ["a", "b"],
            [[new RegionRun(1, 1)], [new RegionRun(0, 2), new RegionRun(1, 1)]]);
        LiftFootprint footprint = new(body.Sha256, 4,
            [new(4, [new(false, [0, 64, 191], [0.011, 0.002, 0.03]), new(true, [63, 65], []), default]),
             new(8, [new(false, [65, 192], [0.02, 0.006])])]);

        Assert.Equal(new double?[] { 0.03, 0.011 }, footprint.RegionHeights(body));
        LiftFootprint swinging = new(body.Sha256, 4, [new(4, [new(true, [0], []), new(false, [64], [0])])]);
        Assert.Equal(new double?[] { 0, null }, swinging.RegionHeights(body));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Region_heights_require_the_same_body_identity_and_triangle_count(bool wrongHash)
    {
        BodyRegionMap body = new("rig-v1:" + Digest, ["a", "b"], [[new RegionRun(1, 1)]]);
        Assert.Equal(new double?[2], new LiftFootprint(body.Sha256, 1, []).RegionHeights(body));
        LiftFootprint broken = wrongHash ? new(Digest, 1, []) : new(body.Sha256, 2, []);
        Assert.Throws<ArgumentException>("body", () => broken.RegionHeights(body));
    }

    [Fact]
    public void Admitted_footprints_own_their_entries_parts_and_triangles()
    {
        int[] keys = [1, 65];
        double[] heights = [0.08, 0.02];
        LiftFootprintTriangle[] triangles = [new(false, keys, heights)];
        LiftFootprintPart[] parts = [new(9, triangles)];
        LiftFootprint footprint = new(Digest, 2, parts);

        keys[0] = 0;
        heights[0] = 0;
        triangles[0] = default;
        parts[0] = new(99, []);

        LiftFootprintPart part = Assert.IsType<LiftFootprintPart>(footprint.PartById(9));
        Assert.Null(footprint.PartById(99));
        Assert.Single(footprint.Parts);
        Assert.Single(part.Triangles);
        Assert.Equal(new[] { 1, 65 }, part.Triangles[0].CellKeys.ToArray());
        Assert.Equal(0.08, part.Triangles[0].HeightMetres(0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.00000001, 1)]
    [InlineData(0.000010000000000001, 2)]
    [InlineData(0.1000001, 10001)]
    [InlineData(0.00051, 51)]
    [InlineData(0.25, 25000)]
    public void Heights_round_up_to_the_least_persistable_metres(double metres, int units)
    {
        var triangle = new LiftFootprintTriangle(false, [0], [metres]);
        Assert.Equal(units, triangle.HeightUnits[0]);
        Assert.Equal(units / 100_000d, triangle.HeightMetres(0));
    }

    [Theory]
    [InlineData("", LiftFootprintRefusal.BodyRegionMapSha256Invalid)]
    [InlineData("0123456789Abcdef0123456789abcdef0123456789abcdef0123456789abcdef", LiftFootprintRefusal.BodyRegionMapSha256Invalid)]
    public void Body_digest_must_be_64_lowercase_hex_characters(string badDigest, LiftFootprintRefusal reason)
    {
        Assert.Equal(Digest, new LiftFootprint(Digest, 1, []).BodyRegionMapSha256);
        var error = Assert.Throws<LiftFootprintDataException>(() => new LiftFootprint(badDigest, 1, []));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(badDigest, error.BodyRegionMapSha256);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(MaxBodyTriangles + 1, MaxBodyTriangles)]
    public void Body_triangle_count_must_fit_the_positive_key_domain(int invalid, int valid)
    {
        Assert.Equal(valid, new LiftFootprint(Digest, valid, []).BodyTriangleCount);
        var error = Assert.Throws<LiftFootprintDataException>(() => new LiftFootprint(Digest, invalid, []));
        Assert.Equal(LiftFootprintRefusal.BodyTriangleCountOutsideRange, error.Reason);
        Assert.Equal(invalid, error.BodyTriangleCount);
    }

    [Theory]
    [InlineData(0u, LiftFootprintRefusal.PartIdZero)]
    [InlineData(3u, LiftFootprintRefusal.PartIdsNotAscending)]
    [InlineData(4u, LiftFootprintRefusal.PartIdsNotAscending)]
    public void Part_ids_must_be_positive_and_strictly_ascending(uint invalidId, LiftFootprintRefusal reason)
    {
        Assert.Equal(new uint[] { 4, 5 }, new LiftFootprint(Digest, 1, [new(4, []), new(5, [])]).Parts.Select(p => p.PartId));
        // A zero ID stands alone so it breaks positivity only. Order cases follow the positive ID 4.
        LiftFootprintPart[] parts = invalidId == 0 ? [new(invalidId, [])] : [new(4, []), new(invalidId, [])];
        var error = Assert.Throws<LiftFootprintDataException>(() => new LiftFootprint(Digest, 1, parts));
        Assert.Equal(reason, error.Reason);
        Assert.Equal((invalidId, parts.Length - 1), (error.PartId, error.PartIndex));
    }

    [Fact]
    public void A_missing_part_names_its_position()
    {
        Assert.NotNull(new LiftFootprint(Digest, 1, [new(8, [])]).PartById(8));
        var error = Assert.Throws<LiftFootprintDataException>(() => new LiftFootprint(Digest, 1, [null!]));
        Assert.Equal(LiftFootprintRefusal.PartMissing, error.Reason);
        Assert.Equal(0, error.PartIndex);
    }

    [Fact]
    public void An_entry_must_be_inside_the_admitted_body()
    {
        static LiftFootprint Create(int key) => new(Digest, 2, [new(2, []), new(7, [default, new(true, [0, key], [])])]);
        Assert.Equal(127, Create(127).Parts[1].Triangles[1].CellKeys[1]);
        var error = Assert.Throws<LiftFootprintDataException>(() => Create(128));
        Assert.Equal(LiftFootprintRefusal.CellKeyOutsideBody, error.Reason);
        Assert.Equal((7u, 1, 1, 1, 128L, 2L),
            (error.PartId, error.PartIndex, error.TriangleIndex, error.EntryIndex, error.CellKey, error.BodyTriangleCount));
    }

    [Theory]
    [InlineData(-1, LiftFootprintRefusal.CellKeyNegative, 0)]
    [InlineData(1, LiftFootprintRefusal.CellKeysNotAscending, 1)]
    [InlineData(2, LiftFootprintRefusal.CellKeysNotAscending, 1)]
    public void Triangle_keys_must_be_nonnegative_and_strictly_ascending(int key, LiftFootprintRefusal reason, int entry)
    {
        Assert.Equal(new[] { 2, 3 }, new LiftFootprintTriangle(true, [2, 3], []).CellKeys.ToArray());
        int[] broken = entry == 0 ? [key, 3] : [2, key];
        var error = Assert.Throws<LiftFootprintDataException>(() => new LiftFootprintTriangle(true, broken, []));
        Assert.Equal(reason, error.Reason);
        Assert.Equal((entry, (long)key), (error.EntryIndex, error.CellKey));
    }

    [Theory]
    [InlineData(true, new[] { 5 }, new[] { 0.1 }, LiftFootprintRefusal.SwingingTriangleHasHeights, null, 0.1)]
    [InlineData(false, new[] { 5, 9 }, new[] { 0.1 }, LiftFootprintRefusal.FixedTriangleHeightCountMismatch, 9L, null)]
    [InlineData(false, new[] { 5 }, new[] { 0.1, 0.2 }, LiftFootprintRefusal.FixedTriangleHeightCountMismatch, null, 0.2)]
    public void Only_fixed_triangles_carry_one_height_per_cell(bool swinging, int[] keys, double[] heights,
        LiftFootprintRefusal reason, long? unmatchedKey, double? unmatchedHeight)
    {
        double[] valid = swinging ? [] : keys.Select(_ => 0.1).ToArray();
        Assert.Equal(valid.Length, new LiftFootprintTriangle(swinging, keys, valid).HeightUnits.Length);
        var error = Assert.Throws<LiftFootprintDataException>(() => new LiftFootprintTriangle(swinging, keys, heights));
        Assert.Equal(reason, error.Reason);
        int entry = swinging ? 0 : Math.Min(keys.Length, heights.Length);
        Assert.Equal((entry, unmatchedKey, unmatchedHeight), (error.EntryIndex, error.CellKey, error.HeightMetres));
    }

    [Theory]
    [InlineData(-0.00001, 0)]
    [InlineData(0.25000001, 0.25)]
    [InlineData(double.NaN, 0.25)]
    [InlineData(double.PositiveInfinity, 0.25)]
    public void Fixed_heights_must_be_finite_and_within_range(double invalid, double valid)
    {
        Assert.Equal(valid, new LiftFootprintTriangle(false, [5], [valid]).HeightMetres(0));
        var error = Assert.Throws<LiftFootprintDataException>(() => new LiftFootprintTriangle(false, [5], [invalid]));
        Assert.Equal(LiftFootprintRefusal.HeightMetresOutsideRange, error.Reason);
        Assert.Equal((0, 5L, invalid), (error.EntryIndex, error.CellKey, error.HeightMetres));
    }
}
