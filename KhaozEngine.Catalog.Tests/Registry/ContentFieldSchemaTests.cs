using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Registry;

/// <summary>
/// The field schema of contracts 4.7: the ordered field list a type registers beside its codec, the
/// durable numbering of <see cref="ContentFieldKind"/>, and the row whose values are PARALLEL to that
/// ordered list rather than keyed by name.
/// </summary>
public class ContentFieldSchemaTests
{
    static ContentFieldEntry Int(string name, bool required = false)
        => new(name, ContentFieldKind.Int, null, ContentVisibility.Client, required);

    [Fact]
    public void FieldKindNumbersArePinnedAndThereAreExactlySeven()
    {
        // catalog_row_field.field_kind stores this with a CHECK (field_kind BETWEEN 0 AND 6), spec 4.4,
        // so the numbering is durable and an insertion in the middle would restate every authored row.
        Assert.Equal(0, (int)ContentFieldKind.Int);
        Assert.Equal(1, (int)ContentFieldKind.ScaledInt);
        Assert.Equal(2, (int)ContentFieldKind.Bool);
        Assert.Equal(3, (int)ContentFieldKind.KeyReference);
        Assert.Equal(4, (int)ContentFieldKind.TagList);
        Assert.Equal(5, (int)ContentFieldKind.LocalizedTextKey);
        Assert.Equal(6, (int)ContentFieldKind.OpaqueBytes);
        Assert.Equal(7, Enum.GetValues<ContentFieldKind>().Length);
    }

    [Fact]
    public void OnlyALocalizedTextKeyIsADerivedMarker()
    {
        foreach (ContentFieldKind kind in Enum.GetValues<ContentFieldKind>())
        {
            string? target = kind switch
            {
                ContentFieldKind.KeyReference => "item",
                ContentFieldKind.TagList => ContentFieldEntry.TagReferenceTarget,
                _ => null,
            };
            var entry = new ContentFieldEntry("f", kind, target, ContentVisibility.Client, false);
            Assert.Equal(kind == ContentFieldKind.LocalizedTextKey, entry.IsDerivedMarker);
        }
    }

    [Fact]
    public void FieldsKeepTheirDeclaredOrder()
    {
        var schema = new ContentFieldSchema(new[] { Int("c"), Int("a"), Int("b") });

        Assert.Equal(new[] { "c", "a", "b" }, schema.Fields.Select(f => f.Name));
    }

    [Fact]
    public void TryGetIsOrdinal()
    {
        var schema = new ContentFieldSchema(new[] { Int("max_stack"), Int("sort") });

        Assert.True(schema.TryGet("max_stack", out ContentFieldEntry? found));
        Assert.NotNull(found);
        Assert.Equal(ContentFieldKind.Int, found.Kind);
        Assert.False(schema.TryGet("Max_Stack", out _));
        Assert.False(schema.TryGet("missing", out _));
    }

    [Fact]
    public void ADuplicateFieldNameIsRefused()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new ContentFieldSchema(new[] { Int("sort"), Int("sort") }));

        Assert.Contains("sort", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentOrBlankFieldNameIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new ContentFieldSchema(null!));
        Assert.Throws<ArgumentException>(() => new ContentFieldSchema(new ContentFieldEntry[] { null! }));
        Assert.Throws<ArgumentException>(() => new ContentFieldSchema(new[] { Int(string.Empty) }));
        Assert.Throws<ArgumentException>(() => new ContentFieldSchema(new[] { Int("  ") }));
    }

    [Fact]
    public void AKeyReferenceNamesTheContentTypeKeyItPointsAt()
    {
        var schema = new ContentFieldSchema(new[]
        {
            new ContentFieldEntry("nested_table", ContentFieldKind.KeyReference, "loot_table", ContentVisibility.ServerOnly, false),
        });

        Assert.Equal("loot_table", schema.Fields[0].ReferenceTarget);

        Assert.Throws<ArgumentException>(() => new ContentFieldSchema(new[]
        {
            new ContentFieldEntry("nested_table", ContentFieldKind.KeyReference, null, ContentVisibility.ServerOnly, false),
        }));
    }

    [Fact]
    public void ATagListTargetSaysItIsTags()
    {
        var schema = new ContentFieldSchema(new[]
        {
            new ContentFieldEntry("tags", ContentFieldKind.TagList, ContentFieldEntry.TagReferenceTarget, ContentVisibility.Client, false),
        });

        Assert.Equal("tag", schema.Fields[0].ReferenceTarget);

        Assert.Throws<ArgumentException>(() => new ContentFieldSchema(new[]
        {
            new ContentFieldEntry("tags", ContentFieldKind.TagList, "item", ContentVisibility.Client, false),
        }));
    }

    [Fact]
    public void EveryOtherKindCarriesNoReferenceTarget()
    {
        Assert.Throws<ArgumentException>(() => new ContentFieldSchema(new[]
        {
            new ContentFieldEntry("sort", ContentFieldKind.Int, "tag", ContentVisibility.Client, false),
        }));
    }

    [Fact]
    public void OnlyAScaledIntCarriesAScale()
    {
        var schema = new ContentFieldSchema(new[]
        {
            new ContentFieldEntry("weight", ContentFieldKind.ScaledInt, null, ContentVisibility.Client, true, Scale: 100),
        });

        Assert.Equal(100, schema.Fields[0].Scale);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentFieldSchema(new[]
        {
            new ContentFieldEntry("weight", ContentFieldKind.ScaledInt, null, ContentVisibility.Client, true, Scale: 0),
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentFieldSchema(new[]
        {
            new ContentFieldEntry("sort", ContentFieldKind.Int, null, ContentVisibility.Client, false, Scale: 100),
        }));
    }

    [Fact]
    public void AFieldValueStoresANumberOrBytesButNeverBoth()
    {
        ContentFieldValue number = ContentFieldValue.OfNumber(ContentFieldKind.Int, -7);
        Assert.Equal(-7L, number.Number);
        Assert.True(number.Bytes.IsEmpty);
        Assert.False(number.IsAbsent);

        ContentFieldValue bytes = ContentFieldValue.OfBytes(ContentFieldKind.TagList, new byte[] { 1, 2, 3 });
        Assert.Equal(3, bytes.Bytes.Length);
        Assert.Equal(0L, bytes.Number);
        Assert.False(bytes.IsAbsent);

        Assert.Throws<ArgumentException>(() => ContentFieldValue.OfNumber(ContentFieldKind.TagList, 1));
        Assert.Throws<ArgumentException>(() => ContentFieldValue.OfBytes(ContentFieldKind.Int, new byte[] { 1 }));
        Assert.Throws<ArgumentException>(() => ContentFieldValue.OfNumber(ContentFieldKind.LocalizedTextKey, 1));
    }

    [Fact]
    public void ARowsValuesAreParallelToTheSchemaAndAMarkerOccupiesItsSlot()
    {
        var schema = new ContentFieldSchema(new[]
        {
            new ContentFieldEntry("name", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true),
            Int("sort"),
        });
        var row = new ContentRow(
            new ContentTypeId(1),
            id: 42,
            key: new ContentKey("metal"),
            parentId: 0,
            isRetired: false,
            fields: new[]
            {
                ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, 3),
            });

        Assert.Equal(schema.Fields.Count, row.Fields.Count);
        Assert.True(schema.Fields[0].IsDerivedMarker);
        Assert.True(row.Fields[0].IsAbsent);
        Assert.Equal(3L, row.Fields[1].Number);
        Assert.Equal("metal", row.Key.ToString());
        Assert.Equal(0, row.ParentId);
        Assert.False(row.IsRetired);
    }

    [Fact]
    public void ARowAcceptsAnIllegalIdSoTheValidatorCanFindIt()
    {
        // KEC0009 refuses an id of 0 or below and KEC0031 refuses a non-zero parent id. Both are FINDINGS,
        // so the row type must be able to hold the bad value or the sweep would never see it.
        var row = new ContentRow(new ContentTypeId(2), 0, new ContentKey("x"), 9, false, Array.Empty<ContentFieldValue>());

        Assert.Equal(0, row.Id);
        Assert.Equal(9, row.ParentId);
    }
}
