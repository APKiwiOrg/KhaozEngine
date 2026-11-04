using System;
using System.Collections.Generic;
using KhaozEngine.Content;
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
    public void Catalog_schema_identifiers_are_owned_read_only_snapshots()
    {
        var finding = new JsonSchemaFinding("/materials/0/steep/chain", "false", "chain");
        var findings = new List<JsonSchemaFinding> { finding };
        var fields = new List<string> { finding.InstanceLocation };
        var ex = new TileWorldException(TileWorldRefusal.CatalogSchemaMismatch, "refused catalog")
        {
            SchemaFindings = findings,
            SchemaFields = fields,
        };
        findings.Clear();
        fields.Clear();
        Assert.NotNull(ex.SchemaFindings);
        Assert.NotNull(ex.SchemaFields);
        Assert.Equal(finding, Assert.Single(ex.SchemaFindings));
        Assert.Equal("/materials/0/steep/chain", Assert.Single(ex.SchemaFields));
        if (ex.SchemaFindings is IList<JsonSchemaFinding> findingList)
        {
            Assert.Throws<NotSupportedException>(() => findingList[0] = finding with { PropertyName = "other" });
            Assert.Throws<NotSupportedException>(() => findingList.Clear());
        }
        if (ex.SchemaFields is IList<string> fieldList)
        {
            Assert.Throws<NotSupportedException>(() => fieldList[0] = "other");
            Assert.Throws<NotSupportedException>(() => fieldList.Clear());
        }
        Assert.Equal(finding, Assert.Single(ex.SchemaFindings));
        Assert.Equal("/materials/0/steep/chain", Assert.Single(ex.SchemaFields));
    }

    [Fact]
    public void Pgm_magic_bytes_are_an_owned_read_only_snapshot()
    {
        byte[] magic = { (byte)'P', (byte)'2' };
        var ex = new TileWorldException(TileWorldRefusal.PgmMagicUnsupported, "refused PGM") { ActualMagic = magic };

        magic[1] = (byte)'6';
        Assert.NotNull(ex.ActualMagic);
        Assert.Equal(new byte[] { (byte)'P', (byte)'2' }, ex.ActualMagic);
        if (ex.ActualMagic is IList<byte> list)
        {
            Assert.Throws<NotSupportedException>(() => list[0] = (byte)'Q');
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
        Assert.Equal(new byte[] { (byte)'P', (byte)'2' }, ex.ActualMagic);
    }

    [Fact]
    public void Required_tools_are_an_owned_read_only_snapshot()
    {
        var source = new List<string> { "clearance_set" };
        var ex = new TileWorldException(TileWorldRefusal.ClearanceBlockMissing, "missing block") { RequiredTools = source };
        source[0] = "other";
        source.Clear();
        Assert.NotNull(ex.RequiredTools);
        Assert.Equal("clearance_set", Assert.Single(ex.RequiredTools));
        if (ex.RequiredTools is IList<string> list)
        {
            Assert.Throws<NotSupportedException>(() => list[0] = "other");
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
        Assert.Equal("clearance_set", Assert.Single(ex.RequiredTools));
    }

    [Fact]
    public void Validation_issues_are_an_owned_read_only_snapshot()
    {
        var issue = new TileWorldIssue("material.missing", "missing material", new RegionCoord(2, 3), new TileCoord(130, 195, 1));
        var source = new List<TileWorldIssue> { issue };
        var ex = new TileWorldException(TileWorldRefusal.WorldValidationFailed, "refused world") { IssueCount = source.Count, Issues = source };

        source[0] = issue with { Code = "other" };
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

    [Fact]
    public void Clearance_overlaps_are_an_owned_read_only_snapshot()
    {
        var overlap = new TileClearanceOverlap("trees-clear-buildings", 11, 22, 0.125f);
        var source = new List<TileClearanceOverlap> { overlap };
        var ex = new TileWorldException(TileWorldRefusal.SessionClearanceFailed, "refused clearance")
        { Overlaps = source };

        source[0] = overlap with { SubjectObjectId = 33 };
        source.Clear();
        Assert.NotNull(ex.Overlaps);
        Assert.Equal(overlap, Assert.Single(ex.Overlaps));
        if (ex.Overlaps is IList<TileClearanceOverlap> list)
        {
            Assert.Throws<NotSupportedException>(() => list[0] = overlap with { ObstacleObjectId = 44 });
            Assert.Throws<NotSupportedException>(() => list.Clear());
        }
        Assert.Equal(overlap, Assert.Single(ex.Overlaps));
    }
}
