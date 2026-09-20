using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// One content type carrying a TAG LIST field, plus the tag vocabulary its ids have to be live rows of. The
/// publish validator refuses a list naming an id no live tag row holds (<c>KEC0008</c>), so the vocabulary is
/// part of the fixture rather than an afterthought of it.
/// <para>
/// The type is GAME band and named for nothing in particular, because the append verb carries no game noun
/// and a fixture that invented one would be testing a game.
/// </para>
/// </summary>
internal static class TagUpgradeFixtures
{
    /// <summary>The game-band type whose rows carry the tag list.</summary>
    public const ushort TaggedTypeId = 1030;

    /// <summary>The tagged type's key.</summary>
    public const string TaggedTypeKey = "tagged_thing";

    /// <summary>The tag-list field the append verb writes.</summary>
    public const string TagsField = "tags";

    /// <summary>The tag every seeded row already carries, which is what this build shipped before.</summary>
    public const int ShippedTag = 1;

    /// <summary>A tag an OPERATOR added, which no build ships and no upgrade may lose or reorder.</summary>
    public const int OperatorTag = 2;

    /// <summary>The tag this build appends, which is the one the upgrade under test is about.</summary>
    public const int NewTag = 3;

    /// <summary>How many tags the vocabulary holds, every one of them live from the first version.</summary>
    public const int TagCount = 3;

    /// <summary>The definition id the append upgrade ships under.</summary>
    public const string UpgradeId = "append-one-tag";

    /// <summary>The first seeded row, which carries the shipped tag only.</summary>
    public const string PlainRow = "plain_row";

    /// <summary>The second seeded row, which an operator has added their own tag to.</summary>
    public const string TunedRow = "tuned_row";

    /// <summary>The tagged type.</summary>
    public static ContentTypeId Tagged => new(TaggedTypeId);

    /// <summary>The engine tag type, whose rows the lists point at.</summary>
    public static ContentTypeId Tag => new(EngineContentTypes.TagTypeId);

    /// <summary>The tagged type's schema: the required int the other fixtures use, then the tag list.</summary>
    public static ContentFieldSchema Schema() => new(
    [
        new ContentFieldEntry(
            PublishFixtures.ValueField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(
            TagsField,
            ContentFieldKind.TagList,
            ContentFieldEntry.TagReferenceTarget,
            ContentVisibility.Client,
            false),
    ]);

    /// <summary>The engine types and the tagged type, which is this build's whole view.</summary>
    public static ContentTypeRegistry Registry()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        ContentFieldSchema schema = Schema();
        registry.RegisterContentType(
            ContentRegistrationBand.Game,
            TaggedTypeId,
            TaggedTypeKey,
            new PublishCodec(Tagged, schema),
            null,
            schema,
            ContentVisibility.Client,
            PublishFixtures.ChunkSlots);
        return registry;
    }

    /// <summary>One tagged row's field edits, with the list left off entirely when it carries no tag.</summary>
    /// <param name="value">The required int field's value.</param>
    /// <param name="tags">The tag ids in authored order.</param>
    public static ContentFieldEdit[] Fields(int value, params int[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var edits = new List<ContentFieldEdit>(2)
        {
            new(PublishFixtures.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, value)),
        };

        if (tags.Length > 0)
        {
            edits.Add(new ContentFieldEdit(TagsField, ContentRowCodecBase.TagListValue(tags)));
        }

        return edits.ToArray();
    }

    /// <summary>One committed tagged row carrying its stable id.</summary>
    /// <param name="id">The stable definition id.</param>
    /// <param name="key">The row's key.</param>
    /// <param name="value">The required int field's value.</param>
    /// <param name="tags">The tag ids in authored order.</param>
    public static ContentBundleRow Row(int id, string key, int value, params int[] tags)
        => new(Tagged, id, new ContentKey(key), false, null, Fields(value, tags));

    /// <summary>One tag row, whose only written field is the console's sort order.</summary>
    /// <param name="id">The tag's stable id.</param>
    public static ContentBundleRow TagRow(int id)
        => new(
            Tag,
            id,
            new ContentKey("tag_" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            false,
            null,
            [new ContentFieldEdit(TagContentType.SortField, ContentFieldValue.OfNumber(ContentFieldKind.Int, id))]);

    /// <summary>
    /// A definition that appends <see cref="NewTag"/> to the named rows, which is the shape a build ships
    /// when a feature gives several existing rows one more tag.
    /// </summary>
    /// <param name="target">The committed target bundle.</param>
    /// <param name="keys">The rows to append to.</param>
    public static ContentUpgradeDefinition AppendsTag(ContentBundle target, params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        return new ContentUpgradeDefinition(
            UpgradeId,
            1,
            "appends one tag to " + string.Join(", ", keys),
            context =>
            {
                var builder = new ContentUpgradePlanBuilder(context, target);
                for (int i = 0; i < keys.Length; i++)
                {
                    builder.AppendTag(Tagged, new ContentKey(keys[i]), TagsField, NewTag);
                }

                return builder.Build();
            });
    }

    /// <summary>
    /// The vocabulary and the two tagged rows as the catalog BEFORE the upgrade: the plain row with the
    /// shipped tag, and the row an operator has added their own tag to, in the order they wrote it.
    /// </summary>
    /// <param name="store">The store to seed.</param>
    public static async Task SeedAsync(IContentAuthoringStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var edits = new List<ContentEdit>(TagCount + 2);
        for (int id = 1; id <= TagCount; id++)
        {
            ContentBundleRow tag = TagRow(id);
            edits.Add(ContentEdit.Import(tag.Type, id, tag.Key, tag.Fields));
        }

        edits.Add(ContentEdit.Import(
            Tagged, 1, new ContentKey(PlainRow), Fields(11, ShippedTag)));
        edits.Add(ContentEdit.Import(
            Tagged, 2, new ContentKey(TunedRow), Fields(22, ShippedTag, OperatorTag)));

        await store.ApplyEditsAsync(
            edits, UpgradeFixtures.Actor, UpgradeFixtures.Operator, "seed the tagged catalog");
        await store.PublishAsync(PublishFixtures.Request(0));
    }

    /// <summary>The tag ids one live row carries, in authored order, read back through the engine's codec.</summary>
    /// <param name="store">The store to read.</param>
    /// <param name="key">The row's key.</param>
    public static async Task<int[]> TagsOfAsync(IContentAuthoringStore store, string key)
    {
        ArgumentNullException.ThrowIfNull(store);

        ContentRowPage page = await store.ListRowsAsync(Tagged, 0, null, true, 0, 50);
        for (int i = 0; i < page.Rows.Count; i++)
        {
            ContentRow row = page.Rows[i];
            if (!string.Equals(row.Key.ToString(), key, StringComparison.Ordinal))
            {
                continue;
            }

            ContentFieldValue value = row.Fields[1];
            var ids = new int[value.Bytes.Length];
            int count = ContentRowCodecBase.ReadTagList(in value, ids);
            return ids[..count];
        }

        return [];
    }
}
