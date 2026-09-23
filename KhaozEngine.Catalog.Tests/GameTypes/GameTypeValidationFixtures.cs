using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using Xunit;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// The registries, rows and assertions the game-type validator tests share. Every candidate is built in
/// memory through <see cref="ContentSnapshotBuilder"/>, with no store and no file, which is the property
/// that makes a validator testable at all.
/// </summary>
/// <remarks>
/// <b>Rows are filled BY FIELD NAME.</b> A test that wrote a positional list would be a second copy of the
/// schema, and the four duration fields are named for the unit under test, so the same fixture serves both
/// spellings and a reordered schema shows up as a missing field rather than as a row whose values slid.
/// </remarks>
static class GameTypeValidationFixtures
{
    /// <summary>
    /// The seven engine types plus all thirteen game types, each registered with NO validator, so a test
    /// drives the one validator it is about and sees nothing else.
    /// </summary>
    internal static ContentTypeRegistry TypeRegistry(ContentDurationUnit unit = ContentDurationUnit.Ticks)
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);

        FoodContentType.Register(registry, unit, validator: null);
        EquipProfileContentType.Register(registry, unit, validator: null);
        EquipStatLineContentType.Register(registry, validator: null);
        StoreContentType.Register(registry, validator: null);
        StoreShelfContentType.Register(registry, validator: null);
        MonsterDropContentType.Register(registry, validator: null);
        GatheringNodeContentType.Register(registry, unit, validator: null);
        RecipeContentType.Register(registry, unit, validator: null);
        RecipeInputContentType.Register(registry, validator: null);
        RecipeOutputContentType.Register(registry, validator: null);
        ToolTierContentType.Register(registry, validator: null);
        SkillCurveContentType.Register(registry, validator: null);
        GameTuningContentType.Register(registry, validator: null);

        return registry;
    }

    /// <summary>One registration by type id, which is where a test reads a schema and a type id from.</summary>
    internal static ContentTypeRegistration Registration(ContentTypeRegistry registry, ushort id)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Assert.True(
            registry.TryGet(new ContentTypeId(id), out ContentTypeRegistration? registration),
            FormattableString.Invariant($"Type {id} is not registered."));
        return registration;
    }

    /// <summary>A row of any registered type, filled BY FIELD NAME, with every other field absent.</summary>
    internal static ContentRow RowOf(
        ContentTypeRegistration registration,
        int id,
        string key,
        params (string Field, ContentFieldValue Value)[] set)
        => RetiredRowOf(registration, id, key, false, set);

    /// <summary>The same, carrying the retired bit, which several rules are statements about.</summary>
    /// <remarks>
    /// Every name handed in must be a field of the schema. A name that matched nothing would leave that field
    /// absent without a word, so a fixture spelled for the wrong unit or a renamed field would build a row
    /// that quietly tests something else.
    /// </remarks>
    internal static ContentRow RetiredRowOf(
        ContentTypeRegistration registration,
        int id,
        string key,
        bool isRetired,
        params (string Field, ContentFieldValue Value)[] set)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(set);

        foreach ((string name, _) in set)
        {
            Assert.True(
                registration.Schema.IndexOf(name) >= 0,
                FormattableString.Invariant(
                    $"Type '{registration.TypeKey}' declares no field '{name}', so the fixture would leave it absent."));
        }

        var values = new ContentFieldValue[registration.Schema.Fields.Count];
        for (int i = 0; i < values.Length; i++)
        {
            ContentFieldEntry field = registration.Schema.Fields[i];
            ContentFieldValue chosen = ContentFieldValue.Absent(field.Kind);
            foreach ((string name, ContentFieldValue value) in set)
            {
                if (string.Equals(name, field.Name, StringComparison.Ordinal))
                {
                    chosen = value;
                }
            }

            values[i] = chosen;
        }

        return new ContentRow(registration.Type, id, new ContentKey(key), 0, isRetired, values);
    }

    /// <summary>A candidate carrying exactly the rows handed in.</summary>
    internal static ContentSnapshot Snapshot(ContentTypeRegistry registry, params ContentRow[] rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var builder = new ContentSnapshotBuilder(registry);
        foreach (ContentRow row in rows)
        {
            builder.AddRow(row);
        }

        return builder.Build();
    }

    /// <summary>One validator's findings, in emit order.</summary>
    internal static List<ContentFinding> Findings(
        IContentValidator validator,
        ContentTypeId type,
        IContentSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(validator);

        var findings = new List<ContentFinding>();
        validator.Validate(type, candidate, findings);
        return findings;
    }

    /// <summary>
    /// One validator's findings as (row id, code) pairs, in emit order, which is what a test asserts against
    /// so the CODE and the row it landed on are both pinned rather than merely the count.
    /// </summary>
    internal static (int Id, string Code)[] Codes(
        IContentValidator validator,
        ContentTypeId type,
        IContentSnapshot candidate)
        => Findings(validator, type, candidate).Select(f => (f.Id, f.Code)).ToArray();

    /// <summary>An engine item row with every required field set, and whatever else a rule reads.</summary>
    internal static ContentRow Item(
        ContentTypeRegistration item,
        int id,
        string key,
        long value = 100,
        bool tradable = true,
        IReadOnlyList<int>? tags = null,
        bool isRetired = false)
        => RetiredRowOf(
            item,
            id,
            key,
            isRetired,
            (ItemContentType.StackableField, Bool(false)),
            (ItemContentType.MaxStackField, Int(1)),
            (ItemContentType.TradableField, Bool(tradable)),
            (ItemContentType.ValueField, Scaled(value)),
            (ItemContentType.TagsField, ContentRowCodecBase.TagListValue(tags ?? [])));

    internal static ContentFieldValue Int(long value) => ContentFieldValue.OfNumber(ContentFieldKind.Int, value);

    internal static ContentFieldValue Ref(long id) => ContentFieldValue.OfNumber(ContentFieldKind.KeyReference, id);

    internal static ContentFieldValue Bool(bool value)
        => ContentFieldValue.OfNumber(ContentFieldKind.Bool, value ? 1 : 0);

    internal static ContentFieldValue Scaled(long value)
        => ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, value);

    /// <summary>
    /// A stored duration in the kind <paramref name="unit"/> declares it: a plain Int of ticks, or a
    /// ScaledInt of hundredths of a second. The kinds are LITERALS rather than the package's own helper, so
    /// a fixture cannot agree with a helper that went wrong.
    /// </summary>
    internal static ContentFieldValue Duration(ContentDurationUnit unit, long stored) => unit switch
    {
        ContentDurationUnit.Ticks => Int(stored),
        ContentDurationUnit.Seconds => Scaled(stored),
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "A fixture duration is Ticks or Seconds."),
    };
}
