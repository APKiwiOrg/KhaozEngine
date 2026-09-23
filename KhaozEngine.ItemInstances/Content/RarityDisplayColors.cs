using System;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>The published display colour of a rarity carried by an item instance. It returns RGB data,
/// leaving the consuming game to choose a drawing type.</summary>
public sealed class RarityDisplayColors
{
    /// <summary>The colour of a plain item or a rarity this client cannot resolve.</summary>
    public const int DefaultRgb = 0xFFFFFF;

    static readonly ContentTypeId RarityType = new(InstanceContentTypeIds.RarityRuleTypeId);

    readonly int[] _rgbById;
    readonly InstancePropertyRegistry _properties;

    RarityDisplayColors(int[] rgbById)
    {
        _rgbById = rgbById;
        _properties = InstancePropertyRegistry.CreateV1();
    }

    /// <summary>Builds one immutable colour lookup over the catalog version this process loaded.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The registered rarity schema has no display colour field.</exception>
    public static RarityDisplayColors Over(ContentRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        int colourIndex = ContentFieldLookup.IndexIn(runtime, RarityType, RarityRuleContentType.DisplayRgbField);
        var rgbById = new int[RarityRuleContentType.MaxDefinitionId + 1];
        Array.Fill(rgbById, DefaultRgb);

        foreach (ContentRow row in runtime.Rows(RarityType))
        {
            if (row.Id <= 0 || row.Id >= rgbById.Length || row.Fields.Count <= colourIndex) continue;
            ContentFieldValue field = row.Fields[colourIndex];
            if (field.IsAbsent || field.Bytes.Length != RarityRuleContentType.DisplayRgbBytes) continue;
            ReadOnlySpan<byte> rgb = field.Bytes.Span;
            rgbById[row.Id] = (rgb[0] << 16) | (rgb[1] << 8) | rgb[2];
        }

        return new RarityDisplayColors(rgbById);
    }

    /// <summary>The colour one canonical payload's rarity identifies. Missing, malformed, quarantined,
    /// and unknown references read white without changing any payload byte.</summary>
    public int RgbOf(ReadOnlySpan<byte> payload)
    {
        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        if (!ItemInstancePayload.TryDecode(_properties, payload, fields, out int count, out _))
            return DefaultRgb;

        for (int i = 0; i < count; i++)
        {
            if (fields[i].Kind != InstancePropertyKind.Rarity || fields[i].BodyLength != 1) continue;
            return _rgbById[payload[fields[i].BodyStart]];
        }

        return DefaultRgb;
    }
}
