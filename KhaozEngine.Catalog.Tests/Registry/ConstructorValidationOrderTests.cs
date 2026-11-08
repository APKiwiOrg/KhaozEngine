using System;
using System.Collections;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Registry;

/// <summary>Argument refusals precede faults from later caller-owned list entries.</summary>
public sealed class ConstructorValidationOrderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_family_refuses_its_key_or_first_block_before_reading_the_next_block(bool blankKey)
    {
        ContentTypeId type = new(1024);
        var wrongSize = new ContentFamilyBlock(1, 0, 32, 32, 33, 1);
        var blocks = blankKey ? new ThrowingTailList<ContentFamilyBlock>() : new ThrowingTailList<ContentFamilyBlock>(wrongSize);

        ArgumentException refused = Assert.Throws<ArgumentException>(() =>
            new ContentFamily(1, type, blankKey ? " " : "items", 16, false, 1, blocks));

        Assert.Equal(blankKey ? "familyKey" : "blocks", refused.ParamName);
        // Fixing that one defect reaches the caller's fault, proving the refusal did not swallow list faults.
        Assert.Throws<InvalidOperationException>(() => new ContentFamily(1, type, "items", blankKey ? 16 : 32, false, 1, blocks));
        Assert.Equal("items", new ContentFamily(1, type, "items", 32, false, 1, [wrongSize]).FamilyKey);
    }

    [Fact]
    public void A_schema_refuses_its_first_field_before_reading_the_next_field()
    {
        var invalid = new ContentFieldEntry(" ", ContentFieldKind.Int, null, ContentVisibility.Client, false);
        var fields = new ThrowingTailList<ContentFieldEntry>(invalid);

        ArgumentException refused = Assert.Throws<ArgumentException>(() => new ContentFieldSchema(fields));

        Assert.Equal("fields", refused.ParamName);
        var valid = invalid with { Name = "n" };
        Assert.Throws<InvalidOperationException>(() => new ContentFieldSchema(new ThrowingTailList<ContentFieldEntry>(valid)));
        Assert.Equal("n", Assert.Single(new ContentFieldSchema([valid]).Fields).Name);
    }

    /// <summary>A valid prefix followed by a caller fault. Enumeration and indexing use the same fault.</summary>
    sealed class ThrowingTailList<T>(params T[] prefix) : IReadOnlyList<T>
    {
        public int Count => prefix.Length + 1;
        public T this[int index] => index < prefix.Length ? prefix[index] : throw new InvalidOperationException("The caller's next entry failed.");
        public IEnumerator<T> GetEnumerator()
        {
            for (int index = 0; index < Count; index++) yield return this[index];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
