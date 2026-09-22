using System;
using System.Collections.Generic;
using System.Reflection;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Authoring;

/// <summary>
/// The reset result as a RECORD OF FACTS THAT AGREE. The one that shipped was a store whose
/// <c>active_version</c> named a row <c>catalog_version</c> did not hold: the number came back set and the
/// operator's line read "nothing published" beside it.
/// <para>
/// <see cref="EveryStateAResetCanProduceIsAcceptedAndSaysOneThing"/> walks every combination the two
/// providers' resets build, and the refusal facts walk the combinations neither of them may build. The
/// reflection fact is what stops a <c>with</c> expression from reaching around both.
/// </para>
/// </summary>
public class ContentCatalogResetResultTests
{
    const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    const string Epoch = "0123456789abcdef0123456789abcdef";

    /// <summary>The schema version the recreate writes in these facts.</summary>
    const int Current = 2;

    const ContentCatalogPriorState Read = ContentCatalogPriorState.Read;

    [Fact]
    public void AVersionNumberWithNoRowBehindItSaysSoRatherThanReadingAsNothingPublished()
    {
        var dangling = new ContentCatalogResetResult(9, null, null, 1, 2, Epoch, Current, Current, Read);

        Assert.Contains("version 9, whose version row was MISSING", dangling.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing published", dangling.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoreThatPublishedNothingSaysNothingPublished()
    {
        var empty = new ContentCatalogResetResult(0, null, null, 0, 0, Epoch, Current, Current, Read);

        Assert.Contains("nothing published", empty.Summary, StringComparison.Ordinal);
        Assert.Contains("0 versions and 0 row revisions", empty.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AVersionThatWasReadNamesItsNumberBothHashesAndBothSchemaVersions()
    {
        var stood = new ContentCatalogResetResult(4, Hash, Hash, 4, 17, Epoch, Current, Current, Read);

        Assert.Contains("version 4, server manifest " + Hash, stood.Summary, StringComparison.Ordinal);
        Assert.Contains("client manifest " + Hash, stood.Summary, StringComparison.Ordinal);
        Assert.Contains("on schema version 2", stood.Summary, StringComparison.Ordinal);
        Assert.Contains("4 versions and 17 row revisions", stood.Summary, StringComparison.Ordinal);
        Assert.Contains("The new store is at schema version 2 with store epoch " + Epoch, stood.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderSchemaThatWasResetSaysWhereItStoodAndWhereItCameBack()
    {
        var older = new ContentCatalogResetResult(1, Hash, Hash, 1, 2, Epoch, 1, Current, Read);

        Assert.Equal(1, older.PriorSchemaVersion);
        Assert.Equal(Current, older.SchemaVersion);
        Assert.Contains("on schema version 1", older.Summary, StringComparison.Ordinal);
        Assert.Contains("The new store is at schema version 2", older.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestHashesUnderVersionZeroAreRefused()
        => Assert.Throws<ArgumentException>(
            () => new ContentCatalogResetResult(0, Hash, Hash, 1, 1, Epoch, Current, Current, Read));

    [Fact]
    public void OneManifestHashWithoutItsPairIsRefused()
    {
        Assert.Throws<ArgumentException>(
            () => new ContentCatalogResetResult(2, Hash, null, 2, 3, Epoch, Current, Current, Read));
        Assert.Throws<ArgumentException>(
            () => new ContentCatalogResetResult(2, null, Hash, 2, 3, Epoch, Current, Current, Read));
    }

    [Fact]
    public void AVersionRowThatWasFoundWithNoVersionDroppedIsRefused()
    {
        // The hashes come off a catalog_version row, so that row was among the versions dropped.
        Assert.Throws<ArgumentException>(
            () => new ContentCatalogResetResult(3, Hash, Hash, 0, 12, Epoch, Current, Current, Read));

        // Rows with no version beside them are NOT refused: a SQLite file whose foreign keys were broken by
        // hand can hold exactly that, and the reset reports what it counted.
        var orphaned = new ContentCatalogResetResult(3, null, null, 0, 12, Epoch, Current, Current, Read);
        Assert.Contains("0 versions and 12 row revisions", orphaned.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ContentCatalogPriorState.Absent)]
    [InlineData(ContentCatalogPriorState.Unreadable)]
    public void AResetThatReadNoCatalogCannotClaimAnythingAboutOne(ContentCatalogPriorState priorState)
    {
        Assert.Throws<ArgumentException>(
            () => new ContentCatalogResetResult(1, null, null, 0, 0, Epoch, 0, Current, priorState));
        Assert.Throws<ArgumentException>(
            () => new ContentCatalogResetResult(1, Hash, Hash, 0, 0, Epoch, 0, Current, priorState));
        Assert.Throws<ArgumentException>(
            () => new ContentCatalogResetResult(0, null, null, 3, 0, Epoch, 0, Current, priorState));
        Assert.Throws<ArgumentException>(
            () => new ContentCatalogResetResult(0, null, null, 0, 5, Epoch, 0, Current, priorState));
        Assert.Throws<ArgumentException>(
            () => new ContentCatalogResetResult(0, null, null, 0, 0, Epoch, 1, Current, priorState));
    }

    [Fact]
    public void ACatalogThatWasReadStoodAtARealSchemaVersionNoNewerThanTheRecreatedOne()
    {
        // Zero is "nothing was read", which contradicts Read. A newer one is refused by every reset before
        // it drops anything, so a result reporting one would be reporting something that cannot have run.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContentCatalogResetResult(0, null, null, 0, 0, Epoch, 0, Current, Read));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContentCatalogResetResult(0, null, null, 0, 0, Epoch, Current + 1, Current, Read));
    }

    [Fact]
    public void TheRecreatedSchemaVersionAndEpochMustBeReal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContentCatalogResetResult(0, null, null, 0, 0, Epoch, 0, 0, ContentCatalogPriorState.Absent));
        Assert.Throws<ArgumentException>(
            () => new ContentCatalogResetResult(0, null, null, 0, 0, string.Empty, 0, Current, ContentCatalogPriorState.Absent));
        Assert.Throws<ArgumentNullException>(
            () => new ContentCatalogResetResult(0, null, null, 0, 0, null!, 0, Current, ContentCatalogPriorState.Absent));
    }

    [Fact]
    public void ANegativeCountOrAnUndeclaredStateIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContentCatalogResetResult(-1, null, null, 0, 0, Epoch, Current, Current, Read));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContentCatalogResetResult(0, null, null, -1, 0, Epoch, Current, Current, Read));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContentCatalogResetResult(0, null, null, 0, -1, Epoch, Current, Current, Read));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContentCatalogResetResult(0, null, null, 0, 0, Epoch, -1, Current, ContentCatalogPriorState.Absent));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContentCatalogResetResult(0, null, null, 0, 0, Epoch, 0, Current, (ContentCatalogPriorState)7));
    }

    /// <summary>
    /// Every property is get-only, so the only way to a value is the constructor that checks it. A single
    /// init accessor would let <c>with</c> move one fact past the rules the others were checked against.
    /// </summary>
    [Fact]
    public void NoPropertyCanBeSetAfterConstructionSoWithCannotBypassTheRules()
    {
        foreach (PropertyInfo property in typeof(ContentCatalogResetResult).GetProperties(
            BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.True(property.SetMethod is null, property.Name + " must not carry a setter or an init accessor.");
        }
    }

    /// <summary>
    /// The states the providers' resets build, one per branch they can take: nothing standing, a partial
    /// catalog forced, and a whole catalog read at the current or the older schema version with nothing
    /// published, a version and both hashes, or a version whose row was missing.
    /// </summary>
    public static IEnumerable<object?[]> ResetStates()
    {
        yield return [0, null, null, 0, 0, 0, ContentCatalogPriorState.Absent, "nothing was dropped"];
        yield return [0, null, null, 0, 0, 0, ContentCatalogPriorState.Unreadable, "what stood was dropped"];
        foreach (int prior in new[] { 1, Current })
        {
            yield return [0, null, null, 0, 0, prior, Read, "nothing published"];
            yield return [3, Hash, Hash, 3, 12, prior, Read, "version 3, server manifest"];
            yield return [3, null, null, 2, 12, prior, Read, "whose version row was MISSING"];
        }
    }

    [Theory]
    [MemberData(nameof(ResetStates))]
    public void EveryStateAResetCanProduceIsAcceptedAndSaysOneThing(
        int active,
        string? server,
        string? client,
        int versions,
        int rows,
        int prior,
        ContentCatalogPriorState state,
        string phrase)
    {
        var result = new ContentCatalogResetResult(active, server, client, versions, rows, Epoch, prior, Current, state);

        Assert.Contains(phrase, result.Summary, StringComparison.Ordinal);
        Assert.EndsWith(
            "The new store is at schema version 2 with store epoch " + Epoch + ".", result.Summary, StringComparison.Ordinal);

        // Each branch's words appear in its own summary and in no other, so no line can say two of them.
        string[] exclusive = ["nothing was dropped", "what stood was dropped", "row revisions were dropped"];
        int said = 0;
        foreach (string words in exclusive)
        {
            said += result.Summary.Contains(words, StringComparison.Ordinal) ? 1 : 0;
        }

        Assert.Equal(1, said);
        Assert.True(result.Summary.Length <= 4096, "The summary is filed in catalog_audit.before_value, which holds 4096.");
    }
}
