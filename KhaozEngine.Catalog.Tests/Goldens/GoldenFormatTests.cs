using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Goldens;

/// <summary>
/// The golden format files of spec 15.1, three assertions each. <b>A golden is added to and NEVER edited.</b>
/// If a golden needs different bytes then the format changed, which means a NEW version directory and a new
/// set beside this one, never a rewrite of these.
/// <para>
/// <b>Decode</b>: the shipped reader produces exactly the values <c>goldens.json</c> records. <b>Hash</b>:
/// the digest over the canonical bytes equals the recorded one, which pins the digest domain, the scheme
/// version and the canonical form at once. <b>Re-encode</b>: encoding the decoded values reproduces the
/// UNCOMPRESSED canonical bytes byte for byte.
/// </para>
/// <para>
/// <b>The compressed bytes are deliberately NOT pinned</b> (spec 7.5). <c>chunk-item-0.kecc</c>,
/// <c>rules.kecr</c> and <c>text-en-us.kect</c> are checked in compressed so the decompression path has a
/// golden, and every assertion is on the DECOMPRESSED result, so a .NET upgrade that changes Brotli's output
/// is not a red test with no defect behind it.
/// </para>
/// </summary>
public sealed class GoldenFormatTests
{
    /// <summary>One case per golden file of every version directory present.</summary>
    public static TheoryData<string, string> Cases() => GoldenLibrary.Cases();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Golden_HashesToTheRecordedDigest(string version, string name)
    {
        GoldenFile golden = GoldenLibrary.Get(version, name);
        Assert.True(GoldenCodecs.TryCanonical(golden.Stored, out byte[] canonical), golden.Path);
        Assert.Equal(golden.CanonicalLength, canonical.Length);

        if (golden.Kind == GoldenCodecs.ManifestKind)
        {
            // A manifest's digest is over its canonical TEXT after decode and never over the file bytes, and
            // each side takes its own sub-domain, so this is also what pins the two sides apart.
            Assert.True(ContentManifestCodec.TryDecode(canonical, Side(golden), out ContentManifest? manifest, out string? reason), reason);
            Assert.Equal(golden.Hash, ContentManifestText.Hash(manifest));
            return;
        }

        Assert.Equal(golden.Hash, ContentHash.OfBytesForKind(canonical));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Golden_DecodesToTheRecordedValues(string version, string name)
    {
        GoldenFile golden = GoldenLibrary.Get(version, name);
        switch (golden.Kind)
        {
            case GoldenCodecs.ChunkKind:
                AssertChunk(golden);
                break;
            case GoldenCodecs.ManifestKind:
                AssertManifest(golden);
                break;
            case GoldenCodecs.RulesKind:
                AssertRules(golden);
                break;
            default:
                AssertText(golden);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Golden_ReEncodesToItsCanonicalBytes(string version, string name)
    {
        GoldenFile golden = GoldenLibrary.Get(version, name);
        GoldenRoundTrip trip = GoldenCodecs.RoundTrip(golden.Stored, Side(golden));
        Assert.True(trip.Decoded, trip.Reason);
        Assert.NotNull(trip.Canonical);
        Assert.NotNull(trip.ReEncoded);
        Assert.Equal(trip.Canonical, trip.ReEncoded);
    }

    [Fact]
    public void EveryFileInEveryVersionDirectory_IsRecorded()
    {
        var missing = new List<string>();
        foreach (string version in GoldenLibrary.Versions)
        {
            var recorded = new HashSet<string>(StringComparer.Ordinal);
            foreach (GoldenFile golden in GoldenLibrary.Of(version))
            {
                recorded.Add(golden.Name);
            }

            foreach (string path in Directory.GetFiles(Path.Combine(GoldenLibrary.Root, version)))
            {
                string file = Path.GetFileName(path);
                if (file != GoldenLibrary.ExpectationsFileName && !recorded.Contains(file))
                {
                    missing.Add(version + "/" + file);
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "A golden file is checked in with no entry in its version's goldens.json, so nothing asserts on it: "
                + string.Join(", ", missing));
    }

    /// <summary>
    /// Every ROW of every golden chunk, decoded through its type's codec and re-encoded, reproduces the body
    /// bytes the golden holds.
    /// <para>
    /// The re-encode assertion above is over the CONTAINER: it rebuilds the chunk from row bodies taken back
    /// verbatim, so it would pass whatever a row codec did with them. This is the row-level statement, and it
    /// is what pins the canonical SHORT encode of an appended field: <c>chunk-item-0.kecc</c> was baked when
    /// <c>item</c> had sixteen fields, and a codec that wrote a seventeenth zero byte for the absent category
    /// would fail here and would break every consumer's pack rebuild the same way.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void GoldenRows_ReEncodeToTheirOwnBodyBytes(string version, string name)
    {
        GoldenFile golden = GoldenLibrary.Get(version, name);
        if (golden.Kind != GoldenCodecs.ChunkKind)
        {
            return;
        }

        ContentTypeRegistry registry = GoldenCodecs.EngineRegistry();
        Assert.True(
            ContentChunkCodec.TryDecode(golden.Stored, registry, out ContentChunk? chunk, out string? reason),
            reason);
        Assert.True(registry.TryGet(chunk.Type, out ContentTypeRegistration? registration));
        Assert.True(chunk.RowCount > 0);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        for (int i = 0; i < chunk.RowCount; i++)
        {
            Assert.True(chunk.TryDecodeRowAt(i, registration.Codec, out ContentRow? row, out reason), reason);
            buffer.ResetWrittenCount();
            registration.Codec.Encode(row, buffer);
            Assert.True(
                chunk.RowBodyAt(i).SequenceEqual(buffer.WrittenSpan),
                string.Create(CultureInfo.InvariantCulture, $"{golden} row {i} did not re-encode to its own bytes"));
        }
    }

    [Fact]
    public void TheWorkedExample_IsTheTagChunkOfSpecSevenNine()
    {
        // Spec 7.9's own numbers, which are the one place the byte layout is written out in prose: 36 header
        // bytes and 25 body bytes, and the digest the section names.
        GoldenFile golden = GoldenLibrary.Get("v1", "chunk-tag-0.kecc");
        Assert.Equal(61, golden.Stored.Length);
        Assert.Equal("30e4bc839a66a488deea911fb794193c4ae35b0be4115f3edbd9e27500b5a2a1", golden.Hash);
    }

    static void AssertChunk(GoldenFile golden)
    {
        JsonElement expected = golden.Values.GetProperty("chunk");
        ContentTypeRegistry registry = GoldenCodecs.EngineRegistry();
        Assert.True(ContentChunkCodec.TryDecode(golden.Stored, registry, out ContentChunk? chunk, out string? reason), reason);

        Assert.Equal(expected.GetProperty("typeId").GetUInt16(), chunk.Type.Value);
        Assert.Equal(expected.GetProperty("chunkIndex").GetInt32(), chunk.ChunkIndex);
        Assert.Equal(expected.GetProperty("slotBase").GetInt32(), chunk.SlotBase);
        Assert.Equal(expected.GetProperty("slotCount").GetInt32(), chunk.SlotCount);
        Assert.Equal(expected.GetProperty("visibility").GetString(), chunk.Visibility.ToString());
        Assert.Equal(expected.GetProperty("rowCount").GetInt32(), chunk.RowCount);
        Assert.Equal(golden.Values.GetProperty("uncompressedBytes").GetInt32(), chunk.Body.Length);

        string typeKey = expected.GetProperty("typeKey").GetString()!;
        Assert.True(registry.TryGetByKey(typeKey, out ContentTypeRegistration? registration));
        Assert.Equal(registration.Type.Value, chunk.Type.Value);

        int index = 0;
        foreach (JsonElement row in expected.GetProperty("rows").EnumerateArray())
        {
            Assert.True(chunk.TryDecodeRowAt(index, registration.Codec, out ContentRow? decoded, out reason), reason);
            Assert.Equal(row.GetProperty("id").GetInt32(), decoded.Id);
            Assert.Equal(row.GetProperty("retired").GetBoolean(), decoded.IsRetired);
            Assert.Equal(row.GetProperty("key").GetString(), Encoding.UTF8.GetString(decoded.Key.Utf8));

            int field = 0;
            foreach (JsonElement value in row.GetProperty("fields").EnumerateArray())
            {
                AssertField(registration.Schema.Fields[field], value, decoded.Fields[field], decoded.Id);
                field++;
            }

            Assert.Equal(registration.Schema.Fields.Count, field);
            index++;
        }

        Assert.Equal(chunk.RowCount, index);
    }

    static void AssertField(ContentFieldEntry entry, JsonElement expected, ContentFieldValue actual, int id)
    {
        string where = string.Create(CultureInfo.InvariantCulture, $"row {id} field '{entry.Name}'");
        Assert.Equal(expected.GetProperty("name").GetString(), entry.Name);
        Assert.Equal(expected.GetProperty("kind").GetString(), entry.Kind.ToString());

        if (expected.TryGetProperty("absent", out JsonElement absent))
        {
            Assert.True(absent.GetBoolean() == actual.IsAbsent, where);
            return;
        }

        Assert.False(actual.IsAbsent, where);
        if (expected.TryGetProperty("tags", out JsonElement tags))
        {
            var ids = new List<uint>();
            int offset = 0;
            while (offset < actual.Bytes.Length)
            {
                Assert.True(ContentVarint.TryRead(actual.Bytes.Span, ref offset, out uint tag, out string? reason), reason);
                ids.Add(tag);
            }

            var wanted = new List<uint>();
            foreach (JsonElement tag in tags.EnumerateArray())
            {
                wanted.Add(tag.GetUInt32());
            }

            Assert.Equal(wanted, ids);
            return;
        }

        if (expected.TryGetProperty("bytesHex", out JsonElement bytes))
        {
            Assert.Equal(bytes.GetString(), Convert.ToHexStringLower(actual.Bytes.Span));
            return;
        }

        Assert.Equal(expected.GetProperty("number").GetInt64(), actual.Number);
    }

    static void AssertManifest(GoldenFile golden)
    {
        JsonElement expected = golden.Values.GetProperty("manifest");
        Assert.True(
            ContentManifestCodec.TryDecode(golden.Stored, Side(golden), out ContentManifest? manifest, out string? reason),
            reason);

        Assert.Equal(expected.GetProperty("side").GetString(), manifest.Side.ToString());
        Assert.Equal(expected.GetProperty("versionNumber").GetUInt32(), manifest.VersionNumber);
        Assert.Equal(expected.GetProperty("formatGeneration").GetUInt32(), manifest.FormatGeneration);
        Assert.Equal(expected.GetProperty("minimumServerBuild").GetUInt32(), manifest.MinimumServerBuild);
        Assert.Equal(expected.GetProperty("minimumClientBuild").GetUInt32(), manifest.MinimumClientBuild);
        Assert.Equal(expected.GetProperty("remapRuleChunkHash").GetString(), manifest.RemapRuleChunkHash);

        int t = 0;
        foreach (JsonElement type in expected.GetProperty("types").EnumerateArray())
        {
            ManifestTypeEntry actual = manifest.Types[t];
            Assert.Equal(type.GetProperty("typeId").GetUInt16(), actual.TypeId);
            Assert.Equal(type.GetProperty("typeKey").GetString(), actual.TypeKey);
            Assert.Equal(type.GetProperty("chunkSlots").GetInt32(), actual.ChunkSlots);
            Assert.Equal(type.GetProperty("visibility").GetString(), actual.Visibility.ToString());

            int c = 0;
            foreach (JsonElement chunk in type.GetProperty("chunks").EnumerateArray())
            {
                Assert.Equal(chunk.GetProperty("chunkIndex").GetUInt32(), actual.Chunks[c].ChunkIndex);
                Assert.Equal(chunk.GetProperty("uncompressedBytes").GetUInt32(), actual.Chunks[c].UncompressedBytes);
                Assert.Equal(chunk.GetProperty("hash").GetString(), actual.Chunks[c].Hash);
                c++;
            }

            Assert.Equal(actual.Chunks.Count, c);
            t++;
        }

        Assert.Equal(manifest.Types.Count, t);

        int l = 0;
        foreach (JsonElement language in expected.GetProperty("languages").EnumerateArray())
        {
            Assert.Equal(language.GetProperty("tag").GetString(), manifest.Languages[l].Tag);
            Assert.Equal(language.GetProperty("textHash").GetString(), manifest.Languages[l].TextHash);
            l++;
        }

        Assert.Equal(manifest.Languages.Count, l);
    }

    static void AssertRules(GoldenFile golden)
    {
        Assert.True(ContentRuleChunkCodec.TryDecode(golden.Stored, out RemapRuleSet? rules, out string? reason), reason);

        int index = 0;
        foreach (JsonElement expected in golden.Values.GetProperty("rules").EnumerateArray())
        {
            RemapRule actual = rules.Rules[index];
            Assert.Equal(expected.GetProperty("sequence").GetInt32(), actual.Sequence);
            Assert.Equal(expected.GetProperty("introducedIn").GetInt32(), actual.IntroducedIn);
            Assert.Equal(expected.GetProperty("typeId").GetUInt16(), actual.Type.Value);
            Assert.Equal(expected.GetProperty("kind").GetString(), actual.Kind.ToString());
            Assert.Equal(expected.GetProperty("fromId").GetInt32(), actual.FromId);
            Assert.Equal(expected.GetProperty("toId").GetInt32(), actual.ToId);
            Assert.Equal(expected.GetProperty("payloadHex").GetString(), Convert.ToHexStringLower(actual.Payload));
            Assert.Equal(expected.GetProperty("destination").GetInt32(), actual.Destination);
            index++;
        }

        Assert.Equal(rules.Rules.Count, index);
    }

    static void AssertText(GoldenFile golden)
    {
        JsonElement expected = golden.Values.GetProperty("text");
        Assert.True(ContentTextChunkCodec.TryDecode(golden.Stored, out ContentTextChunk? chunk, out string? reason), reason);
        Assert.Equal(expected.GetProperty("languageTag").GetString(), chunk.LanguageTag);
        Assert.Equal(expected.GetProperty("entryCount").GetInt32(), chunk.EntryCount);

        var entries = new List<KeyValuePair<string, string>>();
        ContentTextChunkEnumerator walk = chunk.EnumerateEntries();
        while (walk.MoveNext())
        {
            entries.Add(new KeyValuePair<string, string>(
                Encoding.UTF8.GetString(walk.Key),
                Encoding.UTF8.GetString(walk.Value)));
        }

        int index = 0;
        foreach (JsonElement entry in expected.GetProperty("entries").EnumerateArray())
        {
            Assert.Equal(entry.GetProperty("key").GetString(), entries[index].Key);
            Assert.Equal(entry.GetProperty("value").GetString(), entries[index].Value);
            index++;
        }

        Assert.Equal(entries.Count, index);

        // The one string-level re-encode: every entry of a golden is valid UTF-8, so the authored values go
        // back through the encoder and have to reproduce the canonical bytes exactly.
        Assert.True(GoldenCodecs.TryCanonical(golden.Stored, out byte[] canonical));
        Assert.Equal(canonical, ContentTextChunkCodec.Canonical(chunk.LanguageTag, entries));
    }

    static ContentManifestSide Side(GoldenFile golden)
        => golden.Kind == GoldenCodecs.ManifestKind
            && golden.Values.GetProperty("manifest").GetProperty("side").GetString() == nameof(ContentManifestSide.Client)
            ? ContentManifestSide.Client
            : ContentManifestSide.Server;
}
