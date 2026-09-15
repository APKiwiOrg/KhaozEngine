using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Validation;

/// <summary>
/// The rows, registries and assertions the validator tests share. Every candidate is built in memory with
/// no store and no file, through <see cref="ContentSnapshotBuilder"/>, which is the property that makes the
/// validator testable at all.
/// <para>
/// The row builders hand back a CLEAN row by default, so a test names only the one field it is breaking and
/// the reader can see the defect without reading the fixture.
/// </para>
/// </summary>
internal static class ContentValidationFixtures
{
    /// <summary>The id the game-band custom types take, the first id a game is entitled to.</summary>
    public const ushort GameTypeId = 1024;

    /// <summary>The key the game-band custom types take.</summary>
    public const string GameTypeKey = "game_thing";

    public static ContentTypeId ItemType => new(EngineContentTypes.ItemTypeId);

    public static ContentTypeId TagType => new(EngineContentTypes.TagTypeId);

    public static ContentTypeId StatType => new(EngineContentTypes.StatTypeId);

    public static ContentTypeId LootTableType => new(EngineContentTypes.LootTableTypeId);

    public static ContentTypeId LootEntryType => new(EngineContentTypes.LootEntryTypeId);

    public static ContentTypeId GameType => new(GameTypeId);

    /// <summary>A registry carrying the six engine types, fresh, so no test shares one with another.</summary>
    public static ContentTypeRegistry EngineRegistry()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        return registry;
    }

    /// <summary>
    /// A registry carrying ONE game-band type and nothing else, which is what the checks that need a schema
    /// the engine does not own are written against.
    /// </summary>
    public static ContentTypeRegistry GameRegistry(
        ContentFieldSchema schema,
        ContentVisibility visibility = ContentVisibility.Client,
        int chunkSlots = 256,
        int maxRowBytes = ContentPackFormat.DefaultMaxRowBytes,
        int? maxDefinitionId = null,
        IContentValidator? validator = null,
        IContentRowCodec? codec = null,
        string typeKey = GameTypeKey)
    {
        var registry = new ContentTypeRegistry();
        registry.RegisterContentType(
            ContentRegistrationBand.Game,
            GameTypeId,
            typeKey,
            codec ?? new PlainCodec(new ContentTypeId(GameTypeId), schema),
            validator,
            schema,
            visibility,
            chunkSlots,
            maxRowBytes,
            maxDefinitionId);
        return registry;
    }

    /// <summary>One field schema, which is every game-band fixture's whole declaration.</summary>
    public static ContentFieldSchema Schema(params ContentFieldEntry[] fields) => new(fields);

    /// <summary>Runs the sweep the way the three callers do, with nothing ambient anywhere.</summary>
    public static ContentValidationReport Validate(
        ContentSnapshot candidate,
        ContentTypeRegistry registry,
        ContentSnapshot? previous = null,
        IReadOnlyList<RemapRule>? rules = null)
        => ContentValidator.Validate(candidate, previous, rules ?? [], registry);

    /// <summary>A candidate carrying exactly the rows handed in.</summary>
    public static ContentSnapshot Snapshot(ContentTypeRegistry registry, params ContentRow[] rows)
    {
        var builder = new ContentSnapshotBuilder(registry);
        foreach (ContentRow row in rows)
        {
            builder.AddRow(row);
        }

        return builder.Build();
    }

    /// <summary>
    /// The clean candidate: one tag, one item, one stat, one loot table and the entry that draws from it,
    /// with every required field set and every reference resolving.
    /// </summary>
    public static ContentSnapshot CleanCandidate(ContentTypeRegistry registry)
        => Snapshot(
            registry,
            Tag(1, "metal"),
            Item(7, "sword", tagIds: [1]),
            Stat(3, "attack"),
            LootTable(100, "goblin"),
            LootEntry(500, "goblin_sword", table: 100, item: 7));

    /// <summary>
    /// An item row with every required field set: not stackable, one to a slot, tradable and worth nothing.
    /// The three asset references are left absent, so the codec's own character-set rule is not in play.
    /// </summary>
    public static ContentRow Item(
        int id,
        string key,
        bool stackable = false,
        int maxStack = 1,
        int durabilityMax = 0,
        int socketMax = 0,
        int equipProfile = 0,
        IReadOnlyList<int>? tagIds = null,
        int parentId = 0,
        bool isRetired = false)
    {
        var fields = new List<ContentFieldValue>
        {
            ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
            ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
            tagIds is null
                ? ContentFieldValue.Absent(ContentFieldKind.TagList)
                : ContentRowCodecBase.TagListValue(tagIds),
            ContentFieldValue.OfNumber(ContentFieldKind.Bool, stackable ? 1 : 0),
            ContentFieldValue.OfNumber(ContentFieldKind.Int, maxStack),
            ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1),
            ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, 0),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes),
            ContentFieldValue.Absent(ContentFieldKind.Int),
            ContentFieldValue.Absent(ContentFieldKind.ScaledInt),
            ContentFieldValue.Absent(ContentFieldKind.ScaledInt),
            Number(durabilityMax),
            Number(socketMax),
            equipProfile == 0
                ? ContentFieldValue.Absent(ContentFieldKind.KeyReference)
                : ContentFieldValue.OfNumber(ContentFieldKind.KeyReference, equipProfile),
        };

        return new ContentRow(ItemType, id, new ContentKey(key), parentId, isRetired, fields);
    }

    /// <summary>A tag row: the derived name marker and the console's sort order.</summary>
    public static ContentRow Tag(int id, string key, bool isRetired = false)
        => new(
            TagType,
            id,
            new ContentKey(key),
            0,
            isRetired,
            [
                ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
                ContentFieldValue.Absent(ContentFieldKind.Int),
            ]);

    /// <summary>A stat row, scaled by a power of ten with an ascending clamp.</summary>
    public static ContentRow Stat(int id, string key, int scale = 100, int min = 0, int max = 1000)
        => new(
            StatType,
            id,
            new ContentKey(key),
            0,
            false,
            [
                ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, scale),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, min),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, max),
                ContentFieldValue.Absent(ContentFieldKind.TagList),
                ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
            ]);

    /// <summary>A loot table row.</summary>
    public static ContentRow LootTable(int id, string key)
        => new(
            LootTableType,
            id,
            new ContentKey(key),
            0,
            false,
            [
                ContentFieldValue.OfNumber(ContentFieldKind.Int, 1),
                ContentFieldValue.Absent(ContentFieldKind.TagList),
                ContentFieldValue.OfNumber(ContentFieldKind.Bool, 0),
            ]);

    /// <summary>A loot entry row, which names its draw exactly one of three ways.</summary>
    public static ContentRow LootEntry(
        int id,
        string key,
        int table,
        int item = 0,
        int nestedTable = 0,
        IReadOnlyList<int>? requiredTags = null)
        => new(
            LootEntryType,
            id,
            new ContentKey(key),
            0,
            false,
            [
                ContentFieldValue.OfNumber(ContentFieldKind.KeyReference, table),
                Reference(item),
                Reference(nestedTable),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, 1),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, 10000),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, 1),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, 1),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, 0),
                requiredTags is null
                    ? ContentFieldValue.Absent(ContentFieldKind.TagList)
                    : ContentRowCodecBase.TagListValue(requiredTags),
            ]);

    /// <summary>A row of the one-field game-band type, whose value is whatever the test needs.</summary>
    public static ContentRow GameRow(int id, string key, params ContentFieldValue[] values)
        => new(GameType, id, new ContentKey(key), 0, false, values);

    /// <summary>The one finding carrying a code, which fails loudly when there are none or several.</summary>
    public static ContentFinding Single(ContentValidationReport report, string code)
    {
        ContentFinding[] matches = report.Findings
            .Where(finding => string.Equals(finding.Code, code, StringComparison.Ordinal))
            .ToArray();
        Assert.True(
            matches.Length == 1,
            FormattableString.Invariant($"Expected exactly one {code} and got {matches.Length}. {Describe(report)}"));
        return matches[0];
    }

    /// <summary>True when the report carries the code at all.</summary>
    public static bool Has(ContentValidationReport report, string code)
        => report.Findings.Any(finding => string.Equals(finding.Code, code, StringComparison.Ordinal));

    /// <summary>Fails when any of the codes appears, which is how an unreachable code is pinned.</summary>
    public static void AssertNone(ContentValidationReport report, params string[] codes)
    {
        foreach (string code in codes)
        {
            Assert.False(
                Has(report, code),
                FormattableString.Invariant($"Expected no {code}. {Describe(report)}"));
        }
    }

    /// <summary>Every finding, one per line, which is what an assertion message carries.</summary>
    public static string Describe(ContentValidationReport report)
        => "Findings: " + string.Join(
            " | ",
            report.Findings.Select(finding => FormattableString.Invariant(
                $"{finding.Code} type {finding.Type.Value} id {finding.Id}: {finding.Message}")));

    /// <summary>A repeated character run of a given length, for the keys and names that test a bound.</summary>
    public static string Repeat(char c, int count) => new(c, count);

    /// <summary>Opaque bytes of a given length, for the row and chunk size caps.</summary>
    public static ContentFieldValue Blob(int length)
        => ContentFieldValue.OfBytes(ContentFieldKind.OpaqueBytes, new byte[length]);

    /// <summary>The same row with one field value replaced, which is how a test breaks exactly one thing.</summary>
    public static ContentRow WithField(ContentRow row, int index, ContentFieldValue value)
    {
        var fields = new List<ContentFieldValue>(row.Fields);
        fields[index] = value;
        return new ContentRow(row.Type, row.Id, row.Key, row.ParentId, row.IsRetired, fields);
    }

    /// <summary>The same row with one more value than its type's schema declares fields.</summary>
    public static ContentRow WithExtraField(ContentRow row)
    {
        var fields = new List<ContentFieldValue>(row.Fields) { ContentFieldValue.OfNumber(ContentFieldKind.Int, 1) };
        return new ContentRow(row.Type, row.Id, row.Key, row.ParentId, row.IsRetired, fields);
    }

    static ContentFieldValue Number(int value) => value == 0
        ? ContentFieldValue.Absent(ContentFieldKind.Int)
        : ContentFieldValue.OfNumber(ContentFieldKind.Int, value);

    static ContentFieldValue Reference(int value) => value == 0
        ? ContentFieldValue.Absent(ContentFieldKind.KeyReference)
        : ContentFieldValue.OfNumber(ContentFieldKind.KeyReference, value);
}

/// <summary>The generic positional walk with nothing added, which is what a game-band fixture registers.</summary>
internal sealed class PlainCodec(ContentTypeId type, ContentFieldSchema schema) : ContentRowCodecBase(type, schema)
{
}

/// <summary>
/// A codec whose encode and decode DISAGREE by one, which is the defect <c>KEC0027</c> exists to catch
/// before the bytes are hashed into a manifest an operator then treats as an identity. It writes the value
/// plus one and reads what it finds, so a round trip drifts a byte at a time.
/// </summary>
internal sealed class DriftingCodec(ContentTypeId type, string fieldName) : IContentRowCodec
{
    readonly string[] _written = [fieldName];

    public IReadOnlyList<string> WrittenFields => _written;

    public void Encode(ContentRow row, IBufferWriter<byte> destination)
    {
        ReadOnlySpan<byte> key = row.Key.Utf8;
        Span<byte> span = destination.GetSpan(key.Length + 16);
        int written = ContentVarint.Write(span, (uint)key.Length);
        key.CopyTo(span[written..]);
        written += key.Length;
        long value = row.Fields.Count > 0 ? row.Fields[0].Number : 0;
        written += ContentVarint.Write(span[written..], unchecked((uint)(int)(value + 1)));
        destination.Advance(written);
    }

    public bool TryDecode(ReadOnlySpan<byte> body, [MaybeNullWhen(false)] out ContentRow row, out string? reason)
    {
        row = null;
        int offset = 0;
        if (!ContentVarint.TryRead(body, ref offset, out uint length, out reason))
        {
            return false;
        }

        if (length > (uint)(body.Length - offset))
        {
            reason = ContentRowCodecBase.ReasonFieldTruncated;
            return false;
        }

        byte[] key = body.Slice(offset, (int)length).ToArray();
        offset += (int)length;
        if (!ContentVarint.TryRead(body, ref offset, out uint value, out reason))
        {
            return false;
        }

        row = new ContentRow(
            type,
            0,
            new ContentKey(key, 0, key.Length),
            0,
            false,
            [ContentFieldValue.OfNumber(ContentFieldKind.Int, unchecked((int)value))]);
        reason = null;
        return true;
    }
}

/// <summary>A per-type validator that adds one finding of its own, which comes back as <c>KEC0040</c>.</summary>
internal sealed class SpeakingValidator(string code, string message) : IContentValidator
{
    public void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(findings);
        foreach (ContentRow row in candidate.Rows(type))
        {
            findings.Add(new ContentFinding(type, row.Id, code, message));
        }
    }
}

/// <summary>A per-type validator that throws, which is untrusted code behaving the way it must not.</summary>
internal sealed class ThrowingValidator(string message) : IContentValidator
{
    public void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings)
        => throw new InvalidOperationException(message);
}
