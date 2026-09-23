using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The LOOT half of the cross-type sweep: the rules about what a drop table can produce, and the helpers
/// only they use.
/// </summary>
/// <remarks>
/// Two shapes live here. <see cref="CheckLootRows"/> is about NUMBERS NOBODY ROLLS AGAINST, one per half of
/// the engine's composition rule. <see cref="CheckMonsterDropsLeaveOneThing"/> is about how many LINES one
/// kill can leave, which no single row can answer. <see cref="GameContentChecks"/> runs both, in that order,
/// after the tool tier rule and before the required-knob rule.
/// <para>
/// <b>The loot field positions come off the live registry</b>, for the reason
/// <see cref="EngineSchemaFields"/> gives: <c>loot_table</c> and <c>loot_entry</c> are engine types, and a
/// position taken from a schema this package built at type load is this build's idea of them rather than
/// the one the candidate was registered against. A row whose value count does not match its registered
/// schema is skipped, which is the engine's own finding.
/// </para>
/// </remarks>
static class GameLootChecks
{
    /// <summary>
    /// The denominator every <c>chance_bp</c> is out of, so 10000 is a certainty. It is the engine's own
    /// loot scale, which the roller keeps private.
    /// </summary>
    internal const int ChanceBasisPoints = 10_000;

    /// <summary>
    /// The loot numbers NOBODY ROLLS AGAINST. Every drop rate of one table's group adds to 100 percent by
    /// construction, so the way an authored rate goes wrong is a number the roller never reads.
    /// </summary>
    /// <remarks>
    /// The composition rule is the engine's: every <c>guaranteed</c> entry rolls its own <c>chance_bp</c>
    /// first, then the table takes <c>roll_count</c> weighted picks over the non-guaranteed entries, and a
    /// weighted pick does NOT roll <c>chance_bp</c>. Each rule below is one half of that rule written down as
    /// a refusal.
    /// <para>
    /// <b>The engine refuses none of these.</b> Its loot checks are <c>KEC0023</c>, an entry naming its draw
    /// exactly one of three ways, and <c>KEC0024</c>, a cycle through <c>nested_table</c>. Everything else
    /// here is a NUMBER, and the validator's own list of what it deliberately does not check puts "whether a
    /// value is sensible" on the owner.
    /// </para>
    /// <para>
    /// A weighted entry whose chance is also out of range draws the weighted finding alone. Both sentences
    /// are true of it and reporting one row twice for one authored number is noise an author has to read past.
    /// </para>
    /// </remarks>
    internal static void CheckLootRows(
        ContentTypeRegistry registry,
        IContentSnapshot candidate,
        ICollection<ContentFinding> findings)
    {
        var fields = LootFields.In(registry);
        var tableType = new ContentTypeId(EngineContentTypes.LootTableTypeId);
        var entryType = new ContentTypeId(EngineContentTypes.LootEntryTypeId);

        // What each table's pool actually holds, gathered on the entry pass so the table pass can ask.
        var pool = new Dictionary<int, long>();
        var weighted = new HashSet<int>();

        foreach (ContentRow row in candidate.Rows(entryType))
        {
            if (row.IsRetired || row.Fields.Count != fields.EntryFieldCount)
            {
                continue;
            }

            long weight = Number(row, fields.Weight);
            ContentFieldValue chance = Field(row, fields.Chance);
            int table = (int)Number(row, fields.EntryTable);

            if (Number(row, fields.Guaranteed) != 0)
            {
                if (weight != 0)
                {
                    findings.Add(new ContentFinding(
                        entryType,
                        row.Id,
                        GameContentFindings.SweepLootWeightNobodyPicksBy,
                        FormattableString.Invariant(
                            $"Loot entry '{row.Key}' is guaranteed and carries weight {weight}. A guaranteed entry is out of the weighted pool entirely, so no pick can ever land on that weight.")));
                }

                if (!chance.IsAbsent
                    && (chance.Number < 0 || chance.Number > ChanceBasisPoints))
                {
                    // The two halves are not the same defect. Above the scale the entry is a certainty and
                    // below zero it drops nothing, and both are answered without drawing at all.
                    string reads = chance.Number < 0
                        ? "The entry drops nothing and consumes no draw, so the row is a drop no kill can produce."
                        : "The entry is a certainty and consumes no draw, so the authored number is one nobody rolls against.";
                    findings.Add(new ContentFinding(
                        entryType,
                        row.Id,
                        GameContentFindings.SweepLootChanceOutOfRange,
                        FormattableString.Invariant(
                            $"Loot entry '{row.Key}' quotes {chance.Number} basis points, outside 0 to {ChanceBasisPoints}. {reads}")));
                }
                else if (chance.IsAbsent || chance.Number == 0)
                {
                    findings.Add(new ContentFinding(
                        entryType,
                        row.Id,
                        GameContentFindings.SweepLootGuaranteedNeverFires,
                        FormattableString.Invariant(
                            $"Loot entry '{row.Key}' is guaranteed at no chance. A guaranteed entry rolls its own chance and this one never fires, so the row is a drop no kill can produce.")));
                }

                continue;
            }

            if (table > 0)
            {
                weighted.Add(table);
            }

            if (!chance.IsAbsent && chance.Number != ChanceBasisPoints)
            {
                findings.Add(new ContentFinding(
                    entryType,
                    row.Id,
                    GameContentFindings.SweepLootChanceNobodyRolls,
                    FormattableString.Invariant(
                        $"Loot entry '{row.Key}' is a weighted entry quoting {chance.Number} basis points. A pick that won the draw has already had its chance, which is its share of the pool, so the number is one nobody rolls against.")));
            }

            if (weight <= 0)
            {
                findings.Add(new ContentFinding(
                    entryType,
                    row.Id,
                    GameContentFindings.SweepLootEntryNeverPicked,
                    FormattableString.Invariant(
                        $"Loot entry '{row.Key}' is a weighted entry at weight {weight}. A zero-width entry is never the answer to a pick, so the row is an outcome no draw can reach.")));
                continue;
            }

            if (table > 0)
            {
                pool[table] = pool.TryGetValue(table, out long running) ? running + weight : weight;
            }
        }

        foreach (ContentRow row in candidate.Rows(tableType))
        {
            if (row.IsRetired || row.Fields.Count != fields.TableFieldCount)
            {
                continue;
            }

            long rolls = Number(row, fields.RollCount);
            if (rolls > 0 && !pool.ContainsKey(row.Id))
            {
                findings.Add(new ContentFinding(
                    tableType,
                    row.Id,
                    GameContentFindings.SweepLootTableNothingToPick,
                    FormattableString.Invariant(
                        $"Loot table '{row.Key}' asks for {rolls} weighted picks and its pool holds no weight. Every pick draws nothing, so the count is a number nobody rolls against.")));
            }

            if (rolls <= 0 && weighted.Contains(row.Id))
            {
                findings.Add(new ContentFinding(
                    tableType,
                    row.Id,
                    GameContentFindings.SweepLootTableRollsNobodyTakes,
                    FormattableString.Invariant(
                        $"Loot table '{row.Key}' takes no weighted pick and still holds weighted entries. Nothing ever draws over that pool, so the rows are outcomes no kill can produce.")));
            }
        }
    }

    /// <summary>
    /// A kill leaves no more lines than the drops-per-kill knob allows, enforced only on a catalog that
    /// DECLARES the knob.
    /// </summary>
    /// <remarks>
    /// <b>VERSION FOLLOWS CONTENT, for a rule as much as for a format.</b> The knob is read out of the same
    /// snapshot this is sweeping. Absent or retired, this does NOTHING: absence says the catalog was
    /// authored before the rule, and a publish-time rule that judged older content would refuse the seed of
    /// every older baseline and every intermediate version an upgrade chain publishes on its way forward. A
    /// null <paramref name="knob"/> is a game with no such rule, and this does nothing either.
    /// <para>
    /// <b>The rule is a MAXIMUM, computed rather than a shape.</b> A table's most lines is the sum over its
    /// live guaranteed entries of what each can leave, plus <c>roll_count</c> times the most any one of its
    /// weighted entries can leave, because the guaranteed pass rolls all of them and each pick lands on
    /// exactly one. So a table holding a single guaranteed certainty and no pool is ONE and passes.
    /// </para>
    /// <para>
    /// ONE finding per offending MONSTER rather than per table or per entry. What an author fixes is the
    /// tree that monster rolls, and a second finding about the same tree is a second trip round it.
    /// </para>
    /// </remarks>
    internal static void CheckMonsterDropsLeaveOneThing(
        ContentTypeRegistry registry,
        IContentSnapshot candidate,
        GameTuningKnobs knobs,
        string? knob,
        ICollection<ContentFinding> findings)
    {
        var tuningType = new ContentTypeId(GameContentTypeIds.GameTuning);
        if (knob is null || !knobs.TryGet(knob, out long scaled))
        {
            return;
        }

        // A row that is PRESENT and says nothing usable is the opposite of an absent one: somebody typed it.
        // Zero drops allowed is an authoring mistake rather than a rule, and a fraction of a drop is a
        // number no kill could be held against.
        if (scaled < GameTuningContentType.ValueScale || scaled % GameTuningContentType.ValueScale != 0)
        {
            findings.Add(new ContentFinding(
                tuningType,
                0,
                GameContentFindings.SweepMaxDropsPerKillUnusable,
                FormattableString.Invariant(
                    $"The '{knob}' knob is {GameTuningKnobs.Whole(scaled)}, which is not a whole number of drops at or above one. A kill leaves whole lines, so there is nothing for a drop tree to be held against.")));
            return;
        }

        long allowed = scaled / GameTuningContentType.ValueScale;
        var fields = LootFields.In(registry);
        var dropType = new ContentTypeId(GameContentTypeIds.MonsterDrop);
        var entryType = new ContentTypeId(EngineContentTypes.LootEntryTypeId);

        // The live entries by the table they belong to, gathered once. A snapshot is not a runtime and
        // carries no loot index, so the walk below reads the rows themselves.
        var byTable = new Dictionary<int, List<ContentRow>>();
        foreach (ContentRow row in candidate.Rows(entryType))
        {
            if (row.IsRetired || row.Fields.Count != fields.EntryFieldCount)
            {
                continue;
            }

            int table = (int)Number(row, fields.EntryTable);
            if (table <= 0)
            {
                continue;
            }

            if (!byTable.TryGetValue(table, out List<ContentRow>? entries))
            {
                entries = [];
                byTable.Add(table, entries);
            }

            entries.Add(row);
        }

        var walk = new TreeWalk(candidate, fields, byTable);
        foreach (ContentRow row in candidate.Rows(dropType))
        {
            if (row.IsRetired || row.Fields.Count != MonsterDropContentType.FieldCount)
            {
                continue;
            }

            ContentFieldValue table = row.Fields[MonsterDropContentType.LootTableIndex];
            if (table.IsAbsent || table.Number <= 0 || table.Number > int.MaxValue)
            {
                continue;
            }

            TreeSize most = walk.MostLines((int)table.Number, depth: 0);
            if (most.Lines <= allowed)
            {
                continue;
            }

            findings.Add(new ContentFinding(
                dropType,
                row.Id,
                GameContentFindings.SweepMonsterDropsMoreThanOne,
                FormattableString.Invariant(
                    $"Monster drop '{row.Key}' rolls a tree that can leave {most.Lines} lines off one kill, over the '{knob}' knob at {allowed}. The lines come from table '{most.Culprit}'. A kill would drop more than one item.")));
        }
    }

    /// <summary>One loot field, or an absent one for a position the registered schema does not have.</summary>
    static ContentFieldValue Field(ContentRow row, int index)
        => index >= 0 && index < row.Fields.Count
            ? row.Fields[index]
            : ContentFieldValue.Absent(ContentFieldKind.Int);

    /// <summary>One loot field's number, and 0 for an absent one, which is what the engine's index reads.</summary>
    static long Number(ContentRow row, int index)
    {
        ContentFieldValue value = Field(row, index);
        return value.IsAbsent ? 0 : value.Number;
    }

    /// <summary>Two line counts added, clamped at <see cref="long.MaxValue"/> rather than wrapping.</summary>
    /// <remarks>Both sides are at or above zero by construction, so a sum below either of them is a wrap.</remarks>
    static long Sum(long left, long right)
    {
        long total = unchecked(left + right);
        return total < left || total < right ? long.MaxValue : total;
    }

    /// <summary>A roll count times a line count, clamped the same way.</summary>
    static long Product(long rolls, long lines)
    {
        if (rolls <= 0 || lines <= 0)
        {
            return 0;
        }

        return rolls > long.MaxValue / lines ? long.MaxValue : rolls * lines;
    }

    /// <summary>
    /// Where the two engine loot types keep the fields these rules read, off the live registry, and how many
    /// fields a row of each carries. A type the registry does not carry has a count of -1, so no row matches.
    /// </summary>
    readonly record struct LootFields(
        int TableFieldCount,
        int RollCount,
        int EntryFieldCount,
        int EntryTable,
        int Weight,
        int Chance,
        int Guaranteed,
        int Nested)
    {
        internal static LootFields In(ContentTypeRegistry registry)
        {
            ContentFieldSchema? table = SchemaOf(registry, EngineContentTypes.LootTableTypeKey);
            ContentFieldSchema? entry = SchemaOf(registry, EngineContentTypes.LootEntryTypeKey);
            return new LootFields(
                table?.Fields.Count ?? -1,
                table?.IndexOf(LootTableContentType.RollCountField) ?? -1,
                entry?.Fields.Count ?? -1,
                entry?.IndexOf(LootEntryContentType.TableField) ?? -1,
                entry?.IndexOf(LootEntryContentType.WeightField) ?? -1,
                entry?.IndexOf(LootEntryContentType.ChanceBasisPointsField) ?? -1,
                entry?.IndexOf(LootEntryContentType.GuaranteedField) ?? -1,
                entry?.IndexOf(LootEntryContentType.NestedTableField) ?? -1);
        }

        static ContentFieldSchema? SchemaOf(ContentTypeRegistry registry, string key)
            => registry.TryGetByKey(key, out ContentTypeRegistration? registration) ? registration.Schema : null;
    }

    /// <summary>
    /// What one table's tree is worth: the most lines a roll of it can write, and the table to BLAME for
    /// them.
    /// </summary>
    /// <param name="Lines">The maximum, saturating at <see cref="long.MaxValue"/> rather than wrapping.</param>
    /// <param name="Culprit">
    /// The deepest table whose own structure produced the count, which is the one an author has to edit. A
    /// tree three levels deep would otherwise report a root that is innocent of everything but drawing the
    /// table that is not.
    /// </param>
    readonly record struct TreeSize(long Lines, ContentKey Culprit);

    /// <summary>One sweep's walk over the candidate's loot trees, holding what every step of it reads.</summary>
    sealed class TreeWalk(
        IContentSnapshot candidate,
        LootFields fields,
        Dictionary<int, List<ContentRow>> byTable)
    {
        readonly ContentTypeId _tableType = new(EngineContentTypes.LootTableTypeId);

        /// <summary>
        /// The most lines ONE roll of a table can write: every live guaranteed entry's own most, because the
        /// guaranteed pass rolls all of them, plus <c>roll_count</c> times the largest a single weighted entry
        /// can leave, because each pick lands on exactly one of them.
        /// </summary>
        /// <remarks>
        /// The recursion stops where a ROLL stops. A nested entry at <see cref="LootRoller.MaxNestedDepth"/>
        /// is refused by the roller and draws nothing, so it contributes zero, and the same cap is what bounds
        /// a cyclic candidate here: <c>KEC0024</c> is the report for a cycle, and a validator that recursed
        /// forever would be reported as a defective validator instead of letting the real finding through.
        /// <para>
        /// <b>The arithmetic SATURATES.</b> Both numbers are authored, <c>roll_count</c> multiplies at every
        /// nested level, and nothing upstream caps either of them, so a hostile or fat-fingered pack could
        /// reach a product that wraps <see cref="long"/> NEGATIVE and sail under the knob. A maximum that
        /// saturates at <see cref="long.MaxValue"/> is still a refusal, which is the only direction this check
        /// may ever err in.
        /// </para>
        /// <para>
        /// <b>The CULPRIT is the deepest table whose own structure produced the count.</b> A table every one
        /// of whose entries is worth a single line is to blame for its own total. A table with an entry worth
        /// more than one is drawing somebody else's problem, so the blame passes down to that entry's tree,
        /// with one exception: a <c>roll_count</c> ABOVE ONE over a weighted pool is this table multiplying
        /// what it drew, so the total is this table's and the blame stops here.
        /// </para>
        /// </remarks>
        internal TreeSize MostLines(int tableId, int depth)
        {
            if (!candidate.TryGetRow(_tableType, tableId, out ContentRow? table)
                || table.IsRetired
                || table.Fields.Count != fields.TableFieldCount
                || !byTable.TryGetValue(tableId, out List<ContentRow>? entries))
            {
                return default;
            }

            long guaranteed = 0;
            long widestPick = 0;
            TreeSize widestEntry = default;
            for (int i = 0; i < entries.Count; i++)
            {
                ContentRow entry = entries[i];
                TreeSize lines = MostLines(entry, depth);
                bool counted = Number(entry, fields.Guaranteed) != 0;
                if (counted)
                {
                    guaranteed = Sum(guaranteed, lines.Lines);
                }
                else if (Number(entry, fields.Weight) > 0)
                {
                    // A weighted entry at no weight is never the answer to a pick, which is its own finding
                    // and is not a line this table can leave.
                    counted = true;
                    if (lines.Lines > widestPick)
                    {
                        widestPick = lines.Lines;
                    }
                }

                if (counted && lines.Lines > widestEntry.Lines)
                {
                    widestEntry = lines;
                }
            }

            long rolls = Number(table, fields.RollCount);
            long most = rolls > 0 ? Sum(guaranteed, Product(rolls, widestPick)) : guaranteed;

            // A roll_count above one is THIS table's own structure multiplying whatever it draws, so this
            // table answers for the total even when its widest entry is worth more than a line.
            bool rollCountDrives = rolls > 1 && widestPick > 0;
            return widestEntry.Lines > 1 && !rollCountDrives
                ? new TreeSize(most, widestEntry.Culprit)
                : new TreeSize(most, table.Key);
        }

        /// <summary>The most lines ONE entry can leave, which is where a nested draw recurses.</summary>
        /// <remarks>
        /// A guaranteed entry rolls its own <c>chance_bp</c>, so one at or below zero leaves nothing at all
        /// and contributes none. A weighted pick does NOT roll a chance, so the same number on a weighted
        /// entry says nothing about how many lines it leaves.
        /// </remarks>
        TreeSize MostLines(ContentRow entry, int depth)
        {
            if (Number(entry, fields.Guaranteed) != 0 && Number(entry, fields.Chance) <= 0)
            {
                return default;
            }

            long nested = Number(entry, fields.Nested);
            if (nested <= 0 || nested > int.MaxValue)
            {
                // An item entry and a tag-filtered entry both leave exactly one line. An entry naming no draw
                // at all is counted as one too rather than as none: KEC0023 is what reports it, and a maximum
                // that guessed downward would be the one number an author could not act on. It blames no
                // table of its own, because a line is not a table.
                return new TreeSize(1, default);
            }

            return depth >= LootRoller.MaxNestedDepth
                ? default
                : MostLines((int)nested, depth + 1);
        }
    }
}
