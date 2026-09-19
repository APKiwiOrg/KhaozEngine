using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.Tests.Catalog.AzureBlob;

/// <summary>
/// Real pack objects, built through the SHIPPED encoders, so every store fact here is about bytes a
/// publisher could actually have written. Hand rolled bytes would pass the digest check and fail the
/// format check, which is the one pairing a store test must never fake.
/// </summary>
/// <param name="Hash">The object's content address, from the codec's own hash rule.</param>
/// <param name="File">The object as stored.</param>
internal readonly record struct StoredObject(string Hash, byte[] File);

/// <summary>Three small objects of two kinds, enough for a put, a get, a list and a delete.</summary>
internal static class CatalogChunks
{
    /// <summary>A <c>KECT</c> text chunk in English.</summary>
    public static StoredObject English() => Text("en-US", "Bronze sword", "Rune sword");

    /// <summary>A second <c>KECT</c> text chunk, at a different address to the English one.</summary>
    public static StoredObject French() => Text("fr-FR", "Epee de bronze", "Epee runique");

    /// <summary>A <c>KECR</c> remap rule chunk, so the set spans more than one pack kind.</summary>
    public static StoredObject Rules()
    {
        RemapRule[] rules =
        [
            new RemapRule(1, 7, new ContentTypeId(EngineContentTypes.ItemTypeId), RemapRuleKind.ReplacedBy, 5, 3, default),
        ];
        return new StoredObject(ContentRuleChunkCodec.Hash(rules), ContentRuleChunkCodec.Encode(rules));
    }

    static StoredObject Text(string languageTag, string bronze, string rune)
    {
        KeyValuePair<string, string>[] entries =
        [
            new("item.bronze_sword.name", bronze),
            new("item.rune_sword.name", rune),
        ];
        return new StoredObject(
            ContentTextChunkCodec.Hash(languageTag, entries),
            ContentTextChunkCodec.Encode(languageTag, entries));
    }
}
