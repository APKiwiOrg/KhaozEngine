using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>mod_group</c> content type of spec 8.2, an exclusivity group and how many of it one item may
/// carry. Type id <see cref="InstanceContentTypeIds.ModGroupTypeId"/>.
/// <para>
/// Two mods sharing a group with <c>max_per_item</c> of 1 cannot both appear, which is the ordinary
/// exclusivity case, and a group with 2 is a family an item may carry twice. The count is why the group is
/// a row rather than a raw integer on the mod: an integer could name the group and could not carry what
/// the group permits.
/// </para>
/// <para>
/// <c>max_per_item</c> is REQUIRED rather than defaulted in the codec, because spec 8.9's fifth check is
/// that the value is at least 1. An optional field's zero form reads back as ABSENT, so a 0 an author
/// wrote would be indistinguishable from an unset field and the check would have nothing to refuse. The
/// authoring store's column default of 1 is what saves the author the keystroke.
/// </para>
/// </summary>
public static class ModGroupContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table. Groups are few, so the chunk is the smallest legal one.</summary>
    public const int DefaultChunkSlots = 256;

    /// <summary>This type's row cap, which the default covers for a row of one small field.</summary>
    public const int MaxRowBytes = ContentPackFormat.DefaultMaxRowBytes;

    /// <summary>The type level visibility, spec 8.1.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>How many mods of this group one item may carry at once, at least 1.</summary>
    public const string MaxPerItemField = "max_per_item";

    /// <summary>The ordered field list, spec 8.2's prose exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(MaxPerItemField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>
    /// The mod group row codec, which is the generic positional walk with nothing added. The floor of 1 on
    /// <see cref="MaxPerItemField"/> is a validator finding rather than a codec refusal, because a group
    /// carrying 0 has to reach the validator intact to be reported as the authoring mistake it is.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the mod group schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
