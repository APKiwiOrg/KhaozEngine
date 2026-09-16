using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Text;

/// <summary>
/// The layered content string catalog of spec 7.6 and contracts 12.4: content first, then the game's
/// shipped catalog, then the key itself as a visible non-fatal placeholder.
/// <para>
/// The layering ORDER is the fact, and it is not a preference: content is the thing that ships without a
/// client release, so a content string must be able to override a stale shipped one. The other fact is the
/// resident shape, because a language held as a string dictionary is 44 MB where the body plus an index is
/// 9.6 MB, and that is what budget P10 is stated against.
/// </para>
/// </summary>
public class ContentStringCatalogTests
{
    /// <summary>A language chunk built through the shipped encoder, entries sorted the way it requires.</summary>
    internal static ContentTextIndex Language(string tag, params (string Key, string Value)[] entries)
    {
        KeyValuePair<string, string>[] pairs = [.. entries
            .Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Value))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)];
        byte[] file = ContentTextChunkCodec.Encode(tag, pairs);

        Assert.True(ContentTextIndex.TryDecode(file, out ContentTextIndex? index, out string? reason), reason);
        return index;
    }

    /// <summary>
    /// The game's own catalog as this seam sees it: the <c>IStringCatalog.TryGet</c> shape, answering the
    /// KEY on a miss the way the shipped catalog does.
    /// </summary>
    static ContentStringFallback Shipped(Dictionary<string, string> strings)
        => (string key, out string value) =>
        {
            bool found = strings.TryGetValue(key, out string? hit);
            value = hit ?? key;
            return found;
        };

    static ContentStringCatalog Catalog(
        ContentStringFallback? shipped = null,
        params ContentTextIndex[] languages)
        => new(languages, languages[0].LanguageTag, shipped);

    // The key is DERIVED, never authored: <type>.<content key>.<field>. A pack and a catalog that agree on
    // the derivation need no lookup table between them, which is the whole reason contracts 12.1 makes it a
    // concatenation.
    [Fact]
    public void A_content_string_resolves_through_its_derived_key()
    {
        ContentStringCatalog catalog = Catalog(
            null,
            Language("en-US", ("item.bronze_sword.name", "Bronze sword"), ("item.bronze_sword.examine", "It is bronze.")));

        string key = ContentTextKey.Derive("item", Encoding.UTF8.GetBytes("bronze_sword"), "name");

        Assert.Equal("item.bronze_sword.name", key);
        Assert.Equal("Bronze sword", catalog.Get(key));
        Assert.Equal("It is bronze.", catalog.Get("item.bronze_sword.examine"));
    }

    // All three layers in one test, because the ORDER is the claim. Content shadows the shipped string, the
    // shipped one answers what content does not carry, and a key nothing carries comes back as itself.
    [Fact]
    public void Content_is_asked_first_then_the_games_catalog_then_the_key_itself()
    {
        var shipped = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["item.bronze_sword.name"] = "Bronze sword (shipped)",
            ["ui.settings.title"] = "Settings",
        };
        ContentStringCatalog catalog = Catalog(
            Shipped(shipped),
            Language("en-US", ("item.bronze_sword.name", "Bronze sword")));

        Assert.Equal("Bronze sword", catalog.Get("item.bronze_sword.name"));
        Assert.Equal("Settings", catalog.Get("ui.settings.title"));
        Assert.Equal("item.rune_sword.name", catalog.Get("item.rune_sword.name"));
        Assert.False(catalog.TryGet("item.rune_sword.name", out string missing));
        Assert.Equal("item.rune_sword.name", missing);
    }

    // The engine's own strings shadow nothing and content shadows nothing of the engine's: the two sets are
    // disjoint by key, because a content key is DERIVED from a row that exists and an engine key is not. The
    // layering is therefore an answer to "who carries this key", never a contest over one.
    [Fact]
    public void A_shipped_key_no_content_row_derives_is_never_shadowed()
    {
        var shipped = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ui.connect.refused"] = "Connection refused",
        };
        ContentStringCatalog catalog = Catalog(
            Shipped(shipped),
            Language("en-US", ("item.bronze_sword.name", "Bronze sword")));

        Assert.Equal("Connection refused", catalog.Get("ui.connect.refused"));
        Assert.False(catalog.TryGetFromContent("ui.connect.refused", out string content));
        Assert.Equal("ui.connect.refused", content);
    }

    // A language the pack ships PARTLY is the ordinary case, because a translation lands string by string.
    // A miss falls to the language the content was authored in, and then to the key.
    [Fact]
    public void A_missing_language_falls_back_to_the_default_language_then_to_the_key()
    {
        ContentStringCatalog catalog = Catalog(
            null,
            Language("en-US", ("item.bronze_sword.name", "Bronze sword"), ("item.rune_sword.name", "Rune sword")),
            Language("fr-FR", ("item.bronze_sword.name", "Epee de bronze")));

        Assert.True(catalog.SelectLanguage("fr-FR"));
        Assert.Equal("fr-FR", catalog.CurrentLanguage);
        Assert.Equal("en-US", catalog.DefaultLanguage);
        Assert.Equal("Epee de bronze", catalog.Get("item.bronze_sword.name"));
        Assert.Equal("Rune sword", catalog.Get("item.rune_sword.name"));
        Assert.Equal("item.iron_sword.name", catalog.Get("item.iron_sword.name"));
    }

    // A client on en-GB against a pack that ships en resolves through the culture's parent. A culture the
    // content has no translation for leaves the selection ALONE, because the default language is what a
    // player should see until there is one.
    [Fact]
    public void SelectLanguage_walks_the_cultures_parents_and_leaves_the_selection_alone_on_a_miss()
    {
        ContentStringCatalog catalog = Catalog(
            null,
            Language("en", ("item.bronze_sword.name", "Bronze sword")),
            Language("fr-FR", ("item.bronze_sword.name", "Epee de bronze")));

        Assert.True(catalog.SelectLanguage(CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal("en", catalog.CurrentLanguage);

        Assert.True(catalog.SelectLanguage(CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Equal("fr-FR", catalog.CurrentLanguage);

        Assert.False(catalog.SelectLanguage(CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal("fr-FR", catalog.CurrentLanguage);
        Assert.False(catalog.SelectLanguage("zz-ZZ"));
        Assert.Equal("fr-FR", catalog.CurrentLanguage);
    }

    // Contracts 12.3: a malformed translator-authored template is CONTENT arriving as data rather than a
    // caller bug, and a Gui resolves it inside the frame loop with nothing above it to catch. It falls back
    // to the unformatted template, which leaves the text visibly wrong without taking the game down.
    [Fact]
    public void Format_falls_back_to_the_unformatted_template_rather_than_throwing()
    {
        ContentStringCatalog catalog = Catalog(
            null,
            Language(
                "en-US",
                ("item.name_template.rare", "{0} {1} {2}"),
                ("item.name_template.plain", "{0} of the {1}"),
                ("ui.count.items", "{0} items")));

        Assert.Equal("Gloom Greatsword of the Bloodshed", catalog.Format("item.name_template.plain", "Gloom Greatsword", "Bloodshed"));
        Assert.Equal("{0} {1} {2}", catalog.Format("item.name_template.rare", "Gloom"));
        Assert.Equal("{0} {1} {2}", catalog.Format("item.name_template.rare"));
        Assert.Equal("7 items", catalog.Format("ui.count.items", 7));
    }

    // A miss never throws on the format path either: the key itself is the template, and a key carrying no
    // placeholder formats to itself whatever the arguments are.
    [Fact]
    public void Format_of_an_absent_key_formats_the_key_itself()
    {
        ContentStringCatalog catalog = Catalog(null, Language("en-US", ("item.bronze_sword.name", "Bronze sword")));

        Assert.Equal("item.rune_sword.name", catalog.Format("item.rune_sword.name", 1, 2));
        Assert.Equal("a.b.c", ContentStringCatalog.SafeFormat(CultureInfo.InvariantCulture, "a.b.c", null));
        Assert.Equal("{1}", ContentStringCatalog.SafeFormat(CultureInfo.InvariantCulture, "{1}", ["one"]));
    }

    // Spec 7.6's resident shape, by reflection over the fields: the chunk BODY itself plus one open-addressed
    // index of offsets, and no decoded entry at all. A Dictionary<string, string> here would be the 44.2 MB
    // measurement the layout revision replaced, and nothing in a chunk exists as UTF-16 until something asks.
    [Fact]
    public void A_decoded_language_is_the_body_itself_plus_one_index_and_nothing_decoded()
    {
        byte[] file = ContentTextChunkCodec.Encode(
            "en-US",
            [new("item.bronze_sword.name", "Bronze sword"), new("item.rune_sword.name", "Rune sword")]);
        Assert.True(ContentTextChunkCodec.TryDecode(file, out ContentTextChunk? chunk, out string? reason), reason);
        ContentTextIndex index = ContentTextIndex.Over(chunk);

        FieldInfo[] fields = typeof(ContentTextIndex)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.DoesNotContain(fields, field => field.FieldType.IsConstructedGenericType);
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(string[]));
        FieldInfo body = Assert.Single(fields, field => field.FieldType == typeof(byte[]));
        FieldInfo buckets = Assert.Single(fields, field => field.FieldType == typeof(int[]));

        // The body is the chunk's own array, not a copy: a copy would be the whole language twice.
        Assert.Same(chunk.BodyArray, body.GetValue(index));

        int[] table = (int[])buckets.GetValue(index)!;
        Assert.Equal(4, table.Length);
        Assert.Equal(2, table.Count(entry => entry != 0));
        Assert.Equal(2, index.EntryCount);
    }

    // The value is a SLICE of the body, so a caller that only needs bytes never inflates one to UTF-16.
    [Fact]
    public void TryGetUtf8_answers_out_of_the_body_without_materialising_a_string()
    {
        ContentStringCatalog catalog = Catalog(null, Language("en-US", ("item.bronze_sword.name", "Bronze sword")));

        Assert.True(catalog.TryGetUtf8("item.bronze_sword.name"u8, out ReadOnlySpan<byte> value));
        Assert.True(value.SequenceEqual("Bronze sword"u8));
        Assert.False(catalog.TryGetUtf8("item.rune_sword.name"u8, out _));
    }

    // The cache is DIRECT MAPPED at 512 and bounded by construction rather than by a policy, so asking for
    // more distinct keys than it holds evicts rather than grows. That is the whole reason it cannot become
    // the thing budget P10 exists to prevent.
    [Fact]
    public void The_resolved_string_cache_is_bounded_at_512_by_construction()
    {
        (string Key, string Value)[] entries = [.. Enumerable.Range(0, 600)
            .Select(i => (
                Key: FormattableString.Invariant($"item.row_{i:D4}.name"),
                Value: FormattableString.Invariant($"Row {i}")))];
        ContentStringCatalog catalog = Catalog(null, Language("en-US", entries));

        foreach ((string key, string value) in entries)
        {
            Assert.Equal(value, catalog.Get(key));
        }

        Assert.Equal(512, ContentStringCatalog.CacheEntries);
        foreach (FieldInfo field in typeof(ContentStringCatalog)
                     .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                     .Where(field => field.Name.Contains("cache", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Equal(ContentStringCatalog.CacheEntries, ((Array)field.GetValue(catalog)!).Length);
        }
    }

    // A repeat lookup answers from the cache, which is the whole reason the cache exists: materialising per
    // call is right for a settings screen built once and wrong for anything a frame loop touches.
    [Fact]
    public void A_repeat_get_returns_the_same_instance()
    {
        ContentStringCatalog catalog = Catalog(null, Language("en-US", ("item.bronze_sword.name", "Bronze sword")));

        string first = catalog.Get("item.bronze_sword.name");
        string second = catalog.Get("item.bronze_sword.name");

        Assert.Same(first, second);
    }

    // A language past the chunk ceiling SHARDS (spec 7.6), so the catalog takes one index per shard under
    // the one tag and a lookup finds a key in whichever shard carries it.
    [Fact]
    public void A_language_that_sharded_resolves_across_its_shards()
    {
        ContentStringCatalog catalog = Catalog(
            null,
            Language("en-US", ("item.axe.name", "Axe"), ("item.bow.name", "Bow")),
            Language("en-US", ("item.sword.name", "Sword"), ("item.wand.name", "Wand")));

        Assert.Equal("Axe", catalog.Get("item.axe.name"));
        Assert.Equal("Wand", catalog.Get("item.wand.name"));
        Assert.Equal(["en-US"], catalog.Languages);
        Assert.Equal("item.spear.name", catalog.Get("item.spear.name"));
    }

    // A key past the 192 byte bound of contracts 12.2 cannot be in any chunk, so it is a miss taken from the
    // LENGTH rather than a probe, and certainly not a throw out of the encoder.
    [Fact]
    public void A_key_past_the_derived_bound_is_a_miss_rather_than_a_throw()
    {
        ContentStringCatalog catalog = Catalog(null, Language("en-US", ("item.bronze_sword.name", "Bronze sword")));
        string overlong = new('k', ContentTextKey.MaxKeyLength + 1);

        Assert.False(catalog.TryGet(overlong, out string value));
        Assert.Equal(overlong, value);
        Assert.Equal(overlong, catalog.Get(overlong));
    }

    [Fact]
    public void An_empty_language_set_is_refused_and_a_default_that_is_not_there_is_refused()
    {
        ContentTextIndex english = Language("en-US", ("item.bronze_sword.name", "Bronze sword"));

        Assert.Throws<ArgumentException>(() => new ContentStringCatalog([], "en-US"));
        Assert.Throws<ArgumentException>(() => new ContentStringCatalog([english], "fr-FR"));
        Assert.Throws<ArgumentNullException>(() => new ContentStringCatalog(null!, "en-US"));
    }
}

/// <summary>
/// The allocation half, which writes no process state of its own but measures a per-thread counter, so it
/// runs in the assembly's serialized collection beside the other allocation assertions.
/// </summary>
[Collection("AllocSensitive")]
public class ContentStringCatalogAllocationTests
{
    // A resolved string comes back from the cache with nothing allocated: the key is probed as UTF-8 in a
    // stack buffer and the value is the instance the cache already holds. This is the frame-loop path.
    [Fact]
    public void A_repeat_get_allocates_nothing()
    {
        ContentTextIndex english = ContentStringCatalogTests.Language(
            "en-US",
            ("item.bronze_sword.name", "Bronze sword"));
        var catalog = new ContentStringCatalog([english], "en-US");
        Assert.Equal("Bronze sword", catalog.Get("item.bronze_sword.name"));

        CatalogAllocAssert.NoPerCallAllocation(
            "ContentStringCatalog.Get over a cached value",
            () =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    _ = catalog.Get("item.bronze_sword.name");
                }
            });
    }

    // The UTF-8 path never materialises anything at all, cache or no cache.
    [Fact]
    public void TryGetUtf8_allocates_nothing()
    {
        ContentTextIndex english = ContentStringCatalogTests.Language(
            "en-US",
            ("item.bronze_sword.name", "Bronze sword"));
        var catalog = new ContentStringCatalog([english], "en-US");

        CatalogAllocAssert.NoPerCallAllocation(
            "ContentStringCatalog.TryGetUtf8",
            () =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    _ = catalog.TryGetUtf8("item.bronze_sword.name"u8, out _);
                }
            });
    }
}
