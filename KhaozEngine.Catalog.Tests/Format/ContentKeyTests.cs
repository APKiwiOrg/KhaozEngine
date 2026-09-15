using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Format;

/// <summary>
/// The content key of contracts 5.3 as the runtime holds it: a slice of the loaded UTF-8 blob rather than a
/// string, compared ORDINALLY, materialising a string only on demand for a log line, a console response or
/// a validator finding. The character and length rules are the validator's, not the value type's.
/// </summary>
public class ContentKeyTests
{
    [Fact]
    public void AKeyFromAStringEqualsTheSameKeyFromABlobSlice()
    {
        byte[] blob = Encoding.UTF8.GetBytes("__iron_swordiron_ingot__");
        var fromSlice = new ContentKey(blob, 2, "iron_sword".Length);
        var fromString = new ContentKey("iron_sword");

        Assert.Equal(fromString, fromSlice);
        Assert.True(fromSlice == fromString);
        Assert.False(fromSlice != fromString);
        Assert.Equal(fromString.GetHashCode(), fromSlice.GetHashCode());
        Assert.True(fromSlice.Utf8.SequenceEqual(fromString.Utf8));

        var other = new ContentKey(blob, 12, "iron_ingot".Length);
        Assert.NotEqual(fromSlice, other);
    }

    [Fact]
    public void ComparisonIsOrdinal()
    {
        Assert.NotEqual(new ContentKey("Stone"), new ContentKey("stone"));
        Assert.NotEqual(new ContentKey("stone"), new ContentKey("ston"));
        Assert.NotEqual(new ContentKey("stone"), new ContentKey("stone "));
        Assert.Equal(new ContentKey("stone"), new ContentKey("stone"));
    }

    [Fact]
    public void AKeyCanIndexADictionary()
    {
        byte[] blob = Encoding.UTF8.GetBytes("stonetimber");
        var map = new Dictionary<ContentKey, int>
        {
            [new ContentKey("stone")] = 1,
            [new ContentKey("timber")] = 2,
        };

        Assert.Equal(1, map[new ContentKey(blob, 0, 5)]);
        Assert.Equal(2, map[new ContentKey(blob, 5, 6)]);
        Assert.False(map.ContainsKey(new ContentKey("Stone")));
    }

    [Fact]
    public void ToStringMaterialisesOnDemandAndTheTypeHoldsNoStringField()
    {
        byte[] blob = Encoding.UTF8.GetBytes("copper_ore");
        var key = new ContentKey(blob, 0, blob.Length);
        Assert.Equal("copper_ore", key.ToString());

        // Two materialisations are two strings: the type stores none, so there is nothing to cache and
        // nothing to keep alive beyond the blob the runtime already holds.
        FieldInfo[] fields = typeof(ContentKey).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(string));
        Assert.Contains(fields, f => f.FieldType == typeof(byte[]));
    }

    [Fact]
    public void ADefaultKeyIsEmptyAndSafe()
    {
        ContentKey none = default;
        Assert.True(none.IsEmpty);
        Assert.Equal(string.Empty, none.ToString());
        Assert.True(none.Utf8.IsEmpty);
        Assert.Equal(none, new ContentKey(string.Empty));
    }

    [Fact]
    public void ASixtyFourByteKeyIsLegalAndASixtyFiveByteOneIsNotRejectedHere()
    {
        // 64 characters is contracts 5.3's cap, and it is the VALIDATOR's rule (KEC0001), not the value
        // type's. The type carries bytes, so an over-long key reaches the validator intact and is reported
        // rather than being silently truncated or thrown on at construction.
        var atCap = new ContentKey(new string('a', 64));
        Assert.Equal(64, atCap.Utf8.Length);

        var overCap = new ContentKey(new string('a', 65));
        Assert.Equal(65, overCap.Utf8.Length);
        Assert.Equal(new string('a', 65), overCap.ToString());
    }

    [Fact]
    public void ANullBlobOrStringIsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentNullException>(() => new ContentKey(null!));
        Assert.Throws<ArgumentNullException>(() => new ContentKey(null!, 0, 0));
    }

    [Fact]
    public void ASliceOutsideTheBlobIsRejectedAtConstruction()
    {
        byte[] blob = Encoding.UTF8.GetBytes("stone");
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentKey(blob, 4, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentKey(blob, -1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentKey(blob, 0, -1));
    }
}
