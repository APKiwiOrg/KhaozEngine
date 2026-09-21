using System;
using System.Buffers;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// The rows the snapshot and item-view tests build, in one place, because both files need the same
/// registry, the same item schema positions and the same encoded body.
/// </summary>
internal static class CatalogSnapshotFixtures
{
    /// <summary>The item type id, which is the engine's and is fixed at 2.</summary>
    public static ContentTypeId ItemType => new(EngineContentTypes.ItemTypeId);

    /// <summary>The tag type id, the second type the ordering tests need.</summary>
    public static ContentTypeId TagType => new(EngineContentTypes.TagTypeId);

    /// <summary>A registry carrying the six engine types, fresh, so no test shares one with another.</summary>
    public static ContentTypeRegistry Registry()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        return registry;
    }

    /// <summary>The registration for one engine type key.</summary>
    public static ContentTypeRegistration Registration(ContentTypeRegistry registry, string typeKey)
    {
        Assert.True(registry.TryGetByKey(typeKey, out ContentTypeRegistration? registration));
        return registration;
    }

    /// <summary>
    /// An item row carrying the five hot fields the typed view reads, plus enough of the rest that the walk
    /// has real fields to skip: a tag list, a bool, an asset reference and three scaled ints.
    /// </summary>
    public static ContentRow ItemRow(
        int id,
        string key,
        bool stackable,
        int maxStack,
        int durabilityMax,
        int socketMax,
        bool isRetired = false,
        int category = 0)
    {
        var fields = new List<ContentFieldValue>
        {
            ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),         // name, a derived marker
            ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),         // examine, a derived marker
            ContentRowCodecBase.TagListValue([7, 9]),                            // tags
            ContentFieldValue.OfNumber(ContentFieldKind.Bool, stackable ? 1 : 0),
            ContentFieldValue.OfNumber(ContentFieldKind.Int, maxStack),
            ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1),                // tradable
            ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, 250),         // value
            Asset("icon/sword.png"),
            Asset("mesh/sword.glb"),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes),              // held_mesh
            ContentFieldValue.OfNumber(ContentFieldKind.Int, 1),                 // ground_pose
            ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, 15000),       // icon_tilt
            ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, 45000),       // icon_spin
            ContentFieldValue.OfNumber(ContentFieldKind.Int, durabilityMax),
            ContentFieldValue.OfNumber(ContentFieldKind.Int, socketMax),
            ContentFieldValue.Absent(ContentFieldKind.KeyReference),             // equip_profile
            category == 0
                ? ContentFieldValue.Absent(ContentFieldKind.KeyReference)
                : ContentFieldValue.OfNumber(ContentFieldKind.KeyReference, category),
        };

        return new ContentRow(ItemType, id, new ContentKey(key), 0, isRetired, fields);
    }

    /// <summary>A tag row: the derived name marker and the console's sort order.</summary>
    public static ContentRow TagRow(int id, string key, int sort = 0)
        => new(
            TagType,
            id,
            new ContentKey(key),
            0,
            false,
            [
                ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, sort),
            ]);

    /// <summary>The encoded body of a row, which is what a chunk carries and what the typed view reads.</summary>
    public static byte[] Body(ContentTypeRegistry registry, string typeKey, ContentRow row)
    {
        var writer = new ArrayBufferWriter<byte>();
        Registration(registry, typeKey).Codec.Encode(row, writer);
        return writer.WrittenSpan.ToArray();
    }

    static ContentFieldValue Asset(string reference)
        => ContentFieldValue.OfBytes(ContentFieldKind.OpaqueBytes, System.Text.Encoding.UTF8.GetBytes(reference));
}
