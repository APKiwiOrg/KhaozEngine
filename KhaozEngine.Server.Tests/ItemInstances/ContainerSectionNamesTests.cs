using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.WorldStore.Journal;
using Xunit;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>
/// Spec 5.2's section naming: <c>bank/p00</c> through <c>bank/p09</c> for a 1,000 slot bank, <c>bag/p00</c>
/// for a 30 slot bag, <c>worn/p00</c> for an 11 slot worn set.
/// <para>
/// The round trip is the fact that matters, because <see cref="ContainerSectionNames.TryParse"/> is the ONE
/// place a stored name is taken apart and spec 13 row 10 uses it to check a page against the section it
/// arrived in. A name that formats one way and parses another is how a page ends up read as a different
/// page.
/// </para>
/// </summary>
public class ContainerSectionNamesTests
{
    /// <summary>The six page numbers the plan pins: both sides of the padding boundary and one far past it.</summary>
    public static TheoryData<int, string> Pages() => new()
    {
        { 0, "bank/p00" },
        { 9, "bank/p09" },
        { 10, "bank/p10" },
        { 99, "bank/p99" },
        { 100, "bank/p100" },
        { 563, "bank/p563" },
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public void A_page_number_formats_zero_padded_to_two_digits_and_unpadded_above_99(int pageIndex, string expected)
        => Assert.Equal(expected, ContainerSectionNames.Format("bank", pageIndex));

    [Theory]
    [MemberData(nameof(Pages))]
    public void A_formatted_name_parses_back_to_the_container_and_the_page(int pageIndex, string expected)
    {
        Assert.True(ContainerSectionNames.TryParse(expected, out string? container, out int parsed));
        Assert.Equal("bank", container);
        Assert.Equal(pageIndex, parsed);
    }

    [Fact]
    public void The_three_container_names_of_spec_5_2_format_as_the_spec_writes_them()
    {
        Assert.Equal("bank/p00", ContainerSectionNames.Format("bank", 0));
        Assert.Equal("bank/p09", ContainerSectionNames.Format("bank", 9));
        Assert.Equal("bag/p00", ContainerSectionNames.Format("bag", 0));
        Assert.Equal("worn/p00", ContainerSectionNames.Format("worn", 0));
    }

    [Fact]
    public void A_formatted_name_is_a_section_name_the_JOURNAL_accepts()
    {
        // JournalProjectionWrite caps the identity at 128 characters over [A-Za-z0-9._:/-], and the slash is
        // in that set, so nothing needs escaping. The write is the check: its constructor refuses anything
        // else.
        var write = new JournalProjectionWrite(
            "player:42", ContainerSectionNames.Format("bank", 563), "item-container", 2, [1, 2, 3]);
        Assert.Equal("bank/p563", write.SectionName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bank")]
    [InlineData("bank/")]
    [InlineData("bank/p")]
    [InlineData("bank/p0")]
    [InlineData("bank/p007")]
    [InlineData("bank/p09x")]
    [InlineData("bank/q09")]
    [InlineData("/p09")]
    [InlineData("bank/p09/p10")]
    [InlineData("profile")]
    [InlineData("bank/p-1")]
    public void A_name_that_is_not_the_one_this_scheme_writes_is_REFUSED(string name)
    {
        // The parse is CANONICAL rather than tolerant: it accepts exactly what Format writes and nothing
        // else. A tolerant parse would read bank/p007 as page 7 and then disagree with every other reader
        // about which section that page belongs in.
        Assert.False(ContainerSectionNames.TryParse(name, out string? container, out int pageIndex));
        Assert.Null(container);
        Assert.Equal(0, pageIndex);
    }

    [Fact]
    public void A_page_index_no_page_HEADER_could_carry_is_refused_by_both_doors()
    {
        // Container codec version 2 writes the page index and the first slot as uint16 fields, so the page
        // index a section names cannot exceed what a header can declare.
        int tooFar = ItemContainerPage.MaxPageIndex + 1;
        Assert.Throws<System.ArgumentOutOfRangeException>(() => ContainerSectionNames.Format("bank", tooFar));
        Assert.False(ContainerSectionNames.TryParse("bank/p656", out _, out _));
    }

    [Fact]
    public void A_container_name_the_journal_would_refuse_is_refused_HERE_instead()
    {
        Assert.Throws<System.ArgumentException>(() => ContainerSectionNames.Format("bank room", 0));
        Assert.Throws<System.ArgumentException>(() => ContainerSectionNames.Format("bank/deep", 0));
        Assert.Throws<System.ArgumentException>(() => ContainerSectionNames.Format("", 0));

        // The LENGTH half of the same identity rule, which only the character set half used to check. A name
        // this long formats fine and then throws out of the journal, at the far end of a commit, with the
        // batch already closed and its pages already dirty.
        Assert.Throws<System.ArgumentException>(() => ContainerSectionNames.Format(new string('b', 200), 0));
    }

    [Fact]
    public void The_length_bound_is_on_the_NAME_THIS_WRITES_rather_than_on_the_container()
    {
        // The suffix is three characters at page 0 and five at page 100, so bounding the input would leave a
        // container that formats at page 99 and throws at page 100. The bound is on the result.
        const int cap = JournalLimits.EngineMaximumIdentityCharacters;
        string widest = new('b', cap - 4);

        Assert.Equal(cap, ContainerSectionNames.Format(widest, 0).Length);
        Assert.Equal(cap, ContainerSectionNames.Format(widest, 99).Length);
        Assert.Throws<System.ArgumentException>(() => ContainerSectionNames.Format(widest + "b", 0));
        Assert.Throws<System.ArgumentException>(() => ContainerSectionNames.Format(widest, 100));
    }
}
