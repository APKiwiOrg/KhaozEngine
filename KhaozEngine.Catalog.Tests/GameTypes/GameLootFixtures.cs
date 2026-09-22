using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using static KhaozEngine.Tests.Catalog.GameTypes.GameTypeValidationFixtures;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// The loot rows, the drops-per-kill knob and the sweep call the two loot test classes share.
/// </summary>
/// <remarks>
/// The knob NAME is made up, for the reason <see cref="GameContentChecksTests"/> gives: which knobs a world
/// tunes is that world's vocabulary, and the sweep takes the name from the game.
/// </remarks>
static class GameLootFixtures
{
    internal const string DropsKnob = "kill_drop_cap";

    /// <summary>The sweep with only the drops-per-kill rule named, which is every loot rule on.</summary>
    internal static GameContentSweepOptions Options() => new() { MaxDropsPerKillKnob = DropsKnob };

    /// <summary>The whole sweep's findings, in emit order.</summary>
    internal static List<ContentFinding> SweepFindings(
        ContentTypeRegistry registry,
        IContentSnapshot candidate,
        GameContentSweepOptions? options = null)
        => Findings(
            new GameContentChecks(registry, options ?? Options()),
            new ContentTypeId(GameContentTypeIds.Food),
            candidate);

    /// <summary>The whole sweep's findings as (row id, code) pairs, in emit order.</summary>
    internal static (int Id, string Code)[] Sweep(
        ContentTypeRegistry registry,
        IContentSnapshot candidate,
        GameContentSweepOptions? options = null)
        => SweepFindings(registry, candidate, options).Select(f => (f.Id, f.Code)).ToArray();

    /// <summary>One loot table. The roll count is a LONG, because an authored one can be any number the
    /// field holds and the overflow fact writes one that is.</summary>
    internal static ContentRow LootTable(ContentTypeRegistration table, int id, string key, long rollCount)
        => RowOf(table, id, key, (LootTableContentType.RollCountField, Int(rollCount)));

    /// <summary>
    /// One loot entry, with the chance ABSENT when none is passed, which is the shape that says an author
    /// wrote no chance down at all.
    /// </summary>
    internal static ContentRow LootEntry(
        ContentTypeRegistration entry,
        int id,
        string key,
        int table,
        int weight,
        bool guaranteed,
        int? chance,
        int item = 0,
        int nested = 0)
    {
        var fields = new List<(string Field, ContentFieldValue Value)>
        {
            (LootEntryContentType.TableField, Ref(table)),
            (LootEntryContentType.WeightField, Int(weight)),
            (LootEntryContentType.GuaranteedField, Bool(guaranteed)),
            (LootEntryContentType.MinCountField, Int(1)),
            (LootEntryContentType.MaxCountField, Int(1)),
            (LootEntryContentType.SortField, Int(id)),
        };

        if (chance.HasValue)
        {
            fields.Add((LootEntryContentType.ChanceBasisPointsField, Int(chance.Value)));
        }

        if (item > 0)
        {
            fields.Add((LootEntryContentType.ItemField, Ref(item)));
        }

        if (nested > 0)
        {
            fields.Add((LootEntryContentType.NestedTableField, Ref(nested)));
        }

        return RowOf(entry, id, key, fields.ToArray());
    }

    /// <summary>One monster drop rule, live or withdrawn, which is where the walk starts.</summary>
    internal static ContentRow Drop(
        ContentTypeRegistration drop,
        int id,
        string key,
        int kind,
        int table,
        bool retired = false)
        => RetiredRowOf(
            drop,
            id,
            key,
            retired,
            (MonsterDropContentType.MonsterKindField, Int(kind)),
            (MonsterDropContentType.LootTableField, Ref(table)));

    /// <summary>The drops-per-kill row at one STORED value, or nothing for null.</summary>
    internal static ContentRow[] Knob(ContentTypeRegistration tuning, int? stored)
        => stored is int value
            ? [RowOf(tuning, 1, DropsKnob, (GameTuningContentType.ValueField, Scaled(value)))]
            : [];
}
