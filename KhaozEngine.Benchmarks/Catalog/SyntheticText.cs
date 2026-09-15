using System;
using System.Collections.Generic;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// The per-language text entries of spec section 7.6: every localized field of every engine type, keyed by
/// the DERIVED key of contracts 12.1, <c>&lt;type key&gt;.&lt;content key&gt;.&lt;field&gt;</c>. The keys
/// are held and sorted ordinal ascending, which the canonical form requires; the values are derived from
/// the key at encode time so two million strings never have to be resident at once.
/// </summary>
public static class SyntheticText
{
    /// <summary>The entry cap per shard, sized so a shard body stays under the 16 MiB chunk ceiling.</summary>
    public const int MaxShardBodyBytes = 14 * 1024 * 1024;

    public static string[] BuildKeys(SyntheticContentSet content)
    {
        ArgumentNullException.ThrowIfNull(content);
        CatalogBenchmarkConfig config = content.Config;
        int capacity = (config.Definitions * 2) + config.TagCount + (config.StatCount * 2)
            + (config.GameTypeCount * config.GameTypeRowCount);
        var keys = new List<string>(capacity);
        foreach (int id in content.ItemIds)
        {
            string key = content.KeyFor(ContentTypes.Item, id);
            keys.Add("item." + key + ".name");
            keys.Add("item." + key + ".examine");
        }
        foreach (int id in content.TagIds) keys.Add("tag." + content.KeyFor(ContentTypes.Tag, id) + ".name");
        foreach (int id in content.StatIds)
        {
            string key = content.KeyFor(ContentTypes.Stat, id);
            keys.Add("stat." + key + ".name");
            keys.Add("stat." + key + ".display_format");
        }
        for (int t = 0; t < config.GameTypeCount; t++)
        {
            ushort typeId = (ushort)(ContentTypes.FirstGameTypeId + t);
            string typeKey = ContentTypes.GameTypeKey(t);
            foreach (int id in content.GameTypeIds[t]) keys.Add(typeKey + "." + content.KeyFor(typeId, id) + ".name");
        }
        string[] sorted = keys.ToArray();
        Array.Sort(sorted, StringComparer.Ordinal);
        return sorted;
    }

    /// <summary>
    /// Splits the sorted key list into shards whose bodies each stay under the chunk ceiling. One language
    /// chunk holds every entry up to that size; past it a language MUST shard, which is arithmetic rather
    /// than a preference at the stress figure (section 7.6).
    /// </summary>
    public static List<(int Start, int Count)> Shard(string[] sortedKeys, int languageTagBytes)
    {
        ArgumentNullException.ThrowIfNull(sortedKeys);
        var shards = new List<(int, int)>();
        int start = 0;
        long bytes = 8;
        for (int i = 0; i < sortedKeys.Length; i++)
        {
            int entryBytes = 1 + sortedKeys[i].Length + 1 + 60;
            if (bytes + entryBytes > MaxShardBodyBytes - languageTagBytes && i > start)
            {
                shards.Add((start, i - start));
                start = i;
                bytes = 8;
            }
            bytes += entryBytes;
        }
        shards.Add((start, sortedKeys.Length - start));
        return shards;
    }

    /// <summary>
    /// A shard's language tag. One shard keeps the plain tag; more take a BCP-47 private use subtag, which
    /// is the spike's stand-in for the sharding mechanism section 7.6 defers to phase 3.
    /// </summary>
    public static string ShardTag(string languageTag, int shardIndex, int shardCount) =>
        shardCount == 1 ? languageTag : languageTag + "-x-s" + shardIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static List<KeyValuePair<string, string>> Materialize(string[] sortedKeys, int start, int count, string languageTag)
    {
        ArgumentNullException.ThrowIfNull(sortedKeys);
        var entries = new List<KeyValuePair<string, string>>(count);
        for (int i = 0; i < count; i++)
        {
            string key = sortedKeys[start + i];
            entries.Add(new KeyValuePair<string, string>(key, SyntheticContentSet.TextValue(languageTag + "/" + key)));
        }
        return entries;
    }
}
