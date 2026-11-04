using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldExceptionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_constructors_leave_the_reason_unset(bool withInner)
    {
        var inner = new InvalidOperationException();
        var ex = withInner
            ? new TileWorldException("external refusal", inner)
            : new TileWorldException("external refusal");

        Assert.Null(ex.Reason);
        Assert.Same(withInner ? inner : null, ex.InnerException);
    }

    [Fact]
    public void Reason_constructor_keeps_the_original_cause()
    {
        var inner = new InvalidOperationException();
        var ex = new TileWorldException(TileWorldRefusal.RegionHashMismatch, "refused region", inner);

        Assert.Equal(TileWorldRefusal.RegionHashMismatch, ex.Reason);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void Validation_issues_are_an_owned_read_only_snapshot()
    {
        var issue = new TileWorldIssue("material.missing", "missing material", new RegionCoord(2, 3), new TileCoord(130, 195, 1));
        var source = new List<TileWorldIssue> { issue };
        var ex = new TileWorldException(TileWorldRefusal.WorldValidationFailed, "refused world") { IssueCount = source.Count, Issues = source };

        source.Clear();
        Assert.NotNull(ex.Issues);
        Assert.Equal(1, ex.IssueCount);
        Assert.Equal(issue, Assert.Single(ex.Issues));
        if (ex.Issues is IList<TileWorldIssue> list)
        {
            Assert.Throws<NotSupportedException>(() => list[0] = issue with { Code = "other" });
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
        Assert.Equal(issue, Assert.Single(ex.Issues));
    }
}
