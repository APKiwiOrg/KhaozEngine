using System;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Authoring;

/// <summary>
/// The reset result as a RECORD OF FACTS THAT AGREE. Every one of these was constructible before, and the
/// one that shipped was a store whose <c>active_version</c> named a row <c>catalog_version</c> did not hold:
/// the number came back set and the operator's line read "nothing published" beside it.
/// </summary>
public class ContentCatalogResetResultTests
{
    const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    const string Epoch = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void AVersionNumberWithNoRowBehindItSaysSoRatherThanReadingAsNothingPublished()
    {
        var dangling = new ContentCatalogResetResult(9, null, null, 1, 2, Epoch);

        Assert.Contains("version 9, whose version row was MISSING", dangling.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing published", dangling.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoreThatPublishedNothingSaysNothingPublished()
    {
        var empty = new ContentCatalogResetResult(0, null, null, 0, 0, Epoch);

        Assert.Contains("nothing published", empty.Summary, StringComparison.Ordinal);
        Assert.Contains("0 versions and 0 row revisions", empty.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AVersionThatWasReadNamesItsNumberAndBothHashes()
    {
        var stood = new ContentCatalogResetResult(4, Hash, Hash, 4, 17, Epoch);

        Assert.Contains("version 4, server manifest " + Hash, stood.Summary, StringComparison.Ordinal);
        Assert.Contains("client manifest " + Hash, stood.Summary, StringComparison.Ordinal);
        Assert.Contains("4 versions and 17 row revisions", stood.Summary, StringComparison.Ordinal);
        Assert.Contains(Epoch, stood.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestHashesUnderVersionZeroAreRefused()
        => Assert.Throws<ArgumentException>(
            () => new ContentCatalogResetResult(0, Hash, Hash, 1, 1, Epoch));

    [Fact]
    public void OneManifestHashWithoutItsPairIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new ContentCatalogResetResult(2, Hash, null, 2, 3, Epoch));
        Assert.Throws<ArgumentException>(() => new ContentCatalogResetResult(2, null, Hash, 2, 3, Epoch));
    }

    [Theory]
    [InlineData(ContentCatalogPriorState.Absent)]
    [InlineData(ContentCatalogPriorState.Unreadable)]
    public void AResetThatReadNoCatalogCannotClaimAnythingAboutOne(ContentCatalogPriorState priorState)
    {
        Assert.Throws<ArgumentException>(
            () => new ContentCatalogResetResult(1, null, null, 0, 0, Epoch, priorState));
        Assert.Throws<ArgumentException>(
            () => new ContentCatalogResetResult(0, null, null, 3, 0, Epoch, priorState));
        Assert.Throws<ArgumentException>(
            () => new ContentCatalogResetResult(0, null, null, 0, 5, Epoch, priorState));
    }

    [Fact]
    public void ANegativeCountIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContentCatalogResetResult(-1, null, null, 0, 0, Epoch));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContentCatalogResetResult(0, null, null, -1, 0, Epoch));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContentCatalogResetResult(0, null, null, 0, -1, Epoch));
    }

    [Theory]
    [InlineData(ContentCatalogPriorState.Absent, "no catalog at all")]
    [InlineData(ContentCatalogPriorState.Unreadable, "a PARTIAL catalog")]
    public void AResetThatReadNoCatalogSaysWhichOneItWas(ContentCatalogPriorState priorState, string expected)
    {
        var result = new ContentCatalogResetResult(0, null, null, 0, 0, Epoch, priorState);

        Assert.Contains(expected, result.Summary, StringComparison.Ordinal);
        Assert.Contains("nothing was dropped", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The epoch is read after the recreate, so every provider fills it into a record built before the drop.
    /// That copy has to keep the facts the original was checked for.
    /// </summary>
    [Fact]
    public void TheEpochCanBeFilledInAfterwardsWithoutLosingWhatStood()
    {
        ContentCatalogResetResult stood = new ContentCatalogResetResult(4, Hash, Hash, 4, 17, string.Empty)
            with { StoreEpoch = Epoch };

        Assert.Equal(Epoch, stood.StoreEpoch);
        Assert.Equal(4, stood.ActiveVersion);
        Assert.Equal(ContentCatalogPriorState.Read, stood.PriorState);
    }
}
