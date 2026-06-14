using System.Collections.Generic;
using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class GapFillerTests
{
    [Fact]
    public void Mid_gap_holds_previous_frame_with_warning()
    {
        var warnings = new List<string>();
        int[] sources = GapFiller.Resolve(
            "north-west", "walking", new HashSet<int> { 0, 1, 3, 4, 5 }, frameCount: 6,
            strict: false, warnings);

        Assert.Equal(new[] { 0, 1, 1, 3, 4, 5 }, sources); // index 2 held from 1
        Assert.Single(warnings);
        Assert.Contains("frame_002", warnings[0]);
        Assert.Contains("held frame_001", warnings[0]);
    }

    [Fact]
    public void Leading_gap_holds_next_frame_with_warning()
    {
        var warnings = new List<string>();
        int[] sources = GapFiller.Resolve(
            "west", "walking", new HashSet<int> { 1, 2, 3, 4, 5 }, frameCount: 6,
            strict: false, warnings);

        Assert.Equal(new[] { 1, 1, 2, 3, 4, 5 }, sources); // index 0 held from 1 (next)
        Assert.Single(warnings);
        Assert.Contains("frame_000", warnings[0]);
        Assert.Contains("held frame_001", warnings[0]);
    }

    [Fact]
    public void No_gaps_produces_identity_and_no_warnings()
    {
        var warnings = new List<string>();
        int[] sources = GapFiller.Resolve(
            "south", "walking", new HashSet<int> { 0, 1, 2 }, frameCount: 3,
            strict: false, warnings);

        Assert.Equal(new[] { 0, 1, 2 }, sources);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Strict_throws_on_first_gap()
    {
        var warnings = new List<string>();
        var ex = Assert.Throws<AssemblyException>(() => GapFiller.Resolve(
            "north-west", "walking", new HashSet<int> { 0, 1, 3 }, frameCount: 4,
            strict: true, warnings));
        Assert.Contains("frame_002", ex.Message);
    }

    [Fact]
    public void Empty_direction_throws()
    {
        var warnings = new List<string>();
        Assert.Throws<AssemblyException>(() => GapFiller.Resolve(
            "east", "walking", new HashSet<int>(), frameCount: 3, strict: false, warnings));
    }
}
