using System.IO;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gui;
using Xunit;

namespace KhaozEngine.Tests.Gui;

/// <summary>
/// Covers <see cref="PatchNotesParser"/> against the canonical shape from
/// <c>docs/CHANGELOG-STYLE.md</c>, its tolerance edge cases (unknown categories, stray bullets,
/// unbalanced backticks, missing dates, garbage input), and a real fixture (Ruinborne's own
/// <c>PLAY_CHANGELOG.md</c>, embedded as <c>Gui/Fixtures/RuinbornePlayChangelog.md</c>).
/// </summary>
public class PatchNotesParserTests
{
    const string Sample = """
        # MyGame - Player Changelog

        ---

        ## 2026-07-05

        ### Build 0.6.5 (Alpha 006)

        - **Minor**
          - `Copper Alloy Bit` now shows first in the Foundry upgrade list, matching its role as the cheapest
            entry-level bit.
        - **Bug**
          - Fixed ore health dropping sharply at certain depths. Ore health now rises smoothly with depth.

        ---

        ## 2026-07-04

        ### Build 0.6.0 (Alpha 006)

        - **Major**
          - Ores now break into fragments as you mine.
        - **Rebalance**
          - `Depth Contract` now boosts ore fragments gained per mine.

        ---
        """;

    static string LoadFixture(string name)
    {
        var asm = typeof(PatchNotesParserTests).Assembly;
        var resourceName = $"KhaozEngine.Tests.Gui.Fixtures.{name}";
        using Stream? stream = asm.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    [Fact]
    public void CanonicalShape_ParsesFully()
    {
        var doc = PatchNotesParser.Parse(Sample);

        Assert.Equal("MyGame - Player Changelog", doc.Title);
        Assert.Equal(2, doc.Builds.Count);
        Assert.Equal("0.6.5", doc.Builds[0].Version);
        Assert.Equal("Alpha 006", doc.Builds[0].BuildName);
        Assert.Equal("2026-07-05", doc.Builds[0].Date);
        Assert.Equal(new[] { PatchNoteCategory.Minor, PatchNoteCategory.Bug },
            doc.Builds[0].Groups.Select(g => g.Category).ToArray());
    }

    [Fact]
    public void WrappedContinuationLines_JoinIntoOneNoteWithSingleSpace()
    {
        var doc = PatchNotesParser.Parse(Sample);
        var minor = doc.Builds[0].Groups[0];

        Assert.Single(minor.Notes);
        Assert.Contains("cheapest entry-level bit", string.Concat(minor.Notes[0].Spans.Select(s => s.Text)));
    }

    [Fact]
    public void BacktickSpans_SplitCodeFlagSetOrderPreserved()
    {
        var doc = PatchNotesParser.Parse(Sample);
        var minor = doc.Builds[0].Groups[0];
        var spans = minor.Notes[0].Spans;

        Assert.True(spans[0].IsCode);
        Assert.Equal("Copper Alloy Bit", spans[0].Text);
        Assert.False(spans[1].IsCode);
    }

    [Fact]
    public void Balance_IsAliasOfRebalance()
    {
        var doc = PatchNotesParser.Parse("### Build 1.0.0 (X)\n- **Balance**\n  - tweak\n");
        Assert.Equal(PatchNoteCategory.Rebalance, doc.Builds[0].Groups[0].Category);
    }

    [Fact]
    public void UnknownCategoryLabel_DegradesToOther()
    {
        var doc = PatchNotesParser.Parse("### Build 1.0.0 (X)\n- **Wild**\n  - note\n");
        Assert.Equal(PatchNoteCategory.Other, doc.Builds[0].Groups[0].Category);
    }

    [Fact]
    public void NoteBulletsBeforeAnyCategoryLabel_LandInOtherGroup()
    {
        var stray = PatchNotesParser.Parse("### Build 1.0.0 (X)\n  - loose note\n");
        Assert.Equal(PatchNoteCategory.Other, stray.Builds[0].Groups[0].Category);
    }

    [Fact]
    public void UnbalancedBackticks_DegradeToOnePlainSpanNoThrowNoLostText()
    {
        var unb = PatchNotesParser.Parse("### Build 1.0.0 (X)\n- **Bug**\n  - broke `half open\n");
        Assert.All(unb.Builds[0].Groups[0].Notes[0].Spans, s => Assert.False(s.IsCode));
    }

    [Fact]
    public void NullInput_YieldsEmptyDocument() => Assert.True(PatchNotesParser.Parse(null).IsEmpty);

    [Fact]
    public void EmptyInput_YieldsEmptyDocument() => Assert.True(PatchNotesParser.Parse("").IsEmpty);

    [Fact]
    public void GarbageProseInput_YieldsEmptyDocument() =>
        Assert.True(PatchNotesParser.Parse("just some prose\nwith lines").IsEmpty);

    [Fact]
    public void BuildHeaderBeforeAnyDate_StillParsesWithEmptyDate()
    {
        var doc = PatchNotesParser.Parse("### Build 1.0.0 (X)\n- **Bug**\n  - n\n");
        Assert.Equal("", doc.Builds[0].Date);
    }

    [Fact]
    public void RealFixture_RuinborneChangelog_ParsesEveryBuildWithNonEmptyGroups()
    {
        var fixture = LoadFixture("RuinbornePlayChangelog.md");
        var real = PatchNotesParser.Parse(fixture);
        int expected = fixture.Split('\n').Count(l => l.StartsWith("### Build "));

        Assert.Equal(expected, real.Builds.Count);
        Assert.DoesNotContain(real.Builds, b => b.Groups.Count == 0);
    }
}
