using KhaozEngine.BugReports.Client;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client.Sections;

public sealed class ReportTextTests
{
    [Fact]
    public void Text_that_fits_is_the_same_instance()
    {
        string text = new('a', ReportText.MaxUnits);

        Assert.Same(text, ReportText.Bound(text));
    }

    [Fact]
    public void Null_stays_null()
        => Assert.Null(ReportText.Bound(null));

    [Fact]
    public void Long_text_is_cut_to_the_limit_with_an_ellipsis()
        => Assert.Equal(new string('a', 1021) + "...", ReportText.Bound(new string('a', 1025)));

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        string text = new string('a', 1020) + "\U0001F600" + new string('b', 10);

        string bound = ReportText.Bound(text)!;

        Assert.Equal(new string('a', 1020) + "...", bound);
    }
}
