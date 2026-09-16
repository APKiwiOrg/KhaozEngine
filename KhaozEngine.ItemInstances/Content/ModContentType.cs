using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>mod</c> content type of spec 8.2, the central affix type: one affix, its kind, its group and its
/// display line. Type id <see cref="InstanceContentTypeIds.ModTypeId"/>.
/// <para>
/// <b>A mod row carries NO tiers, no lines and no weights.</b> They are three child types
/// (<c>mod_tier</c>, <c>stat_line</c>, <c>mod_tier_weight</c>), because any repeating child structure is
/// its own content type with a key reference to its parent, never a blob field.
/// </para>
/// <para>
/// <c>kind</c> is an integer the engine assigns exactly two meanings to, 1 prefix and 2 suffix, with 3 to
/// 255 free for the game. The engine needs the split because the prefix-and-suffix distinction sits on the
/// ROW rather than in the stored affix entry, so the generator reads it to count against the rarity rule's
/// two limits. Everything above 2 is a kind the generator treats as its own counted pool, which is how an
/// implicit, a corruption line or a material line gets a slot without an engine change.
/// </para>
/// <para>
/// <c>group_id</c> is exclusivity with one level of indirection rather than a raw integer, so the group can
/// carry a count. A mod with no group is unconstrained beyond the rarity rule's counts, which is why the
/// field is OPTIONAL.
/// </para>
/// <para>
/// <c>legacy</c> is read by exactly three places: the generator skips a legacy row when building its
/// candidate tables, the crafting guard refuses one, and the frozen-entry rule refuses to rewrite an affix
/// already sitting on a legacy row. Everywhere else a legacy mod resolves through the ordinary path.
/// </para>
/// <para>
/// <c>line</c> stores NOTHING. The localized text key kind is a MARKER whose presence declares that the
/// key derived from the row exists in the text chunks, so a mod's displayed line is looked up rather than
/// stored and a translator adding a language adds a text chunk rather than a content edit. The derived key
/// for the row <c>fine_crafted</c> is <c>mod.fine_crafted.line</c> and can be nothing else.
/// </para>
/// </summary>
public static class ModContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table.</summary>
    public const int DefaultChunkSlots = 4096;

    /// <summary>This type's row cap, which the default covers for a row of four small fields.</summary>
    public const int MaxRowBytes = ContentPackFormat.DefaultMaxRowBytes;

    /// <summary>The type level visibility, spec 8.1. A client computes tooltips from this data.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The prefix kind, the first of the two meanings the engine assigns <see cref="KindField"/>.</summary>
    public const int PrefixKind = 1;

    /// <summary>The suffix kind, the second of the two. Everything above it is the game's own pool.</summary>
    public const int SuffixKind = 2;

    /// <summary>Prefix, suffix, or a game kind above them.</summary>
    public const string KindField = "kind";

    /// <summary>The exclusivity group this mod belongs to, or absent when it belongs to none.</summary>
    public const string GroupIdField = "group_id";

    /// <summary>Whether the mod is kept for stored items only and never rolls again.</summary>
    public const string LegacyField = "legacy";

    /// <summary>The derived display line, <c>mod.&lt;content key&gt;.line</c>, carrying no bytes.</summary>
    public const string LineField = "line";

    /// <summary>The ordered field list, spec 8.2's table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(KindField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(
            GroupIdField,
            ContentFieldKind.KeyReference,
            InstanceContentTypeIds.ModGroupTypeKey,
            ContentVisibility.Client,
            false),
        new ContentFieldEntry(LegacyField, ContentFieldKind.Bool, null, ContentVisibility.Client, true),
        new ContentFieldEntry(LineField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true),
    ]);

    /// <summary>
    /// The mod row codec, which is the generic positional walk with nothing added. Every rule a mod row is
    /// held to is a CROSS-ROW one (a group that resolves, a kind the rarity rules can place), so it belongs
    /// to the validator rather than here.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the mod schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
