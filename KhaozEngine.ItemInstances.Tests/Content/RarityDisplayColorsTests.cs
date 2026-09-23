using System;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceValidationFixtures;

namespace KhaozEngine.Tests.ItemInstances.Content;

/// <summary>The client-visible colour read over a published rarity row and a canonical item payload.</summary>
public sealed class RarityDisplayColorsTests
{
    [Fact]
    public void Rarity_on_an_item_reads_its_published_rgb_including_a_retired_rule()
    {
        RarityDisplayColors colours = RarityDisplayColors.Over(Runtime());

        Assert.Equal(0x75B7F0, colours.RgbOf(Payload(1, withItemLevel: true)));
        Assert.Equal(0xFF9E7A, colours.RgbOf(Payload(2)));
    }

    [Fact]
    public void Missing_rarity_or_missing_authored_colour_reads_white()
    {
        RarityDisplayColors colours = RarityDisplayColors.Over(Runtime());

        Assert.Equal(0xFFFFFF, colours.RgbOf(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0xFFFFFF, colours.RgbOf(Payload(null, withItemLevel: true)));
        Assert.Equal(0xFFFFFF, colours.RgbOf(Payload(3)));
        Assert.Equal(0xFFFFFF, colours.RgbOf(Payload(250)));
    }

    [Fact]
    public void Malformed_or_quarantined_bytes_read_white_without_mutation()
    {
        RarityDisplayColors colours = RarityDisplayColors.Over(Runtime());
        byte[] truncated = [0x82, 0x01, 0x01];
        byte[] original = (byte[])truncated.Clone();
        byte[] quarantined = QuarantineWrapper.Wrap(
            InstanceQuarantineReason.UnknownDefinition, 1, Payload(1));

        Assert.Equal(0xFFFFFF, colours.RgbOf(truncated));
        Assert.Equal(original, truncated);
        Assert.Equal(0xFFFFFF, colours.RgbOf(quarantined));
    }

    static ContentRuntime Runtime()
    {
        ContentTypeRegistry registry = Registry();
        ContentRow blue = RarityRule(registry, 1, "blue", displayRgb: [0x75, 0xB7, 0xF0]);
        ContentRow coral = RarityRule(registry, 2, "coral", displayRgb: [0xFF, 0x9E, 0x7A]);
        ContentRow retired = new(coral.Type, coral.Id, coral.Key, coral.ParentId, true, coral.Fields);
        ContentRow uncoloured = RarityRule(registry, 3, "plain");
        return ContentRuntime.FromSnapshot(Snapshot(registry, blue, retired, uncoloured), registry);
    }

    static byte[] Payload(byte? rarityId, bool withItemLevel = false)
    {
        var builder = new ItemInstancePayloadBuilder();
        if (withItemLevel) builder.AddScalar(InstancePropertyKind.ItemLevel, 42);
        if (rarityId is { } id) builder.AddByte(InstancePropertyKind.Rarity, id);
        byte[] bytes = new byte[builder.Length];
        ItemInstancePayload.Encode(builder, bytes);
        return bytes;
    }
}
