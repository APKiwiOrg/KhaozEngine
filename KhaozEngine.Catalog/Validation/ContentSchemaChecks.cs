using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// Pass 2 of the sweep, SCHEMA (spec 5.3): every field against its type's declared field list, the required
/// fields, the value ranges the engine types declare, the derived localization key shape, and the
/// inheritance guard.
/// <para>
/// A row's values are PARALLEL BY INDEX to its type's schema, so "a field the schema does not declare" is a
/// row that is longer than the schema or one that carries a value at a derived marker's slot, and "a missing
/// field" is a slot the row left absent or never reached.
/// </para>
/// </summary>
internal static class ContentSchemaChecks
{
    /// <summary>The inheritance chain cap of spec 3.8, which <c>KEC0032</c> refuses a walk past.</summary>
    internal const int MaxParentChain = 8;

    internal static void Run(ContentValidationRun run)
    {
        foreach (ContentTypeRegistration registration in run.Registry.ByTypeId)
        {
            foreach (ContentRow row in run.Candidate.Rows(registration.Type))
            {
                CheckFields(run, registration, row);
                CheckInheritance(run, registration, row);
            }
        }

        CheckStatRanges(run);
        CheckStackShape(run);
    }

    /// <summary>
    /// <c>KEC0004</c> on a field the schema does not declare, <c>KEC0005</c> on a live row missing a
    /// required one, and <c>KEC0030</c> on a derived localization key that would not fit its bound.
    /// </summary>
    static void CheckFields(ContentValidationRun run, ContentTypeRegistration registration, ContentRow row)
    {
        IReadOnlyList<ContentFieldEntry> fields = registration.Schema.Fields;
        if (row.Fields.Count > fields.Count)
        {
            run.Add(
                registration.Type,
                row.Id,
                "KEC0004",
                FormattableString.Invariant(
                    $"Row {row.Id} carries {row.Fields.Count} field values and type '{registration.TypeKey}' declares {fields.Count}. A row is parallel by index to its type's schema, so a longer row names a field nothing reads."));
        }

        for (int i = 0; i < fields.Count; i++)
        {
            ContentFieldEntry field = fields[i];
            ContentFieldValue value = ContentValidationRun.Value(row, i, field.Kind);

            if (field.IsDerivedMarker)
            {
                if (!value.IsAbsent)
                {
                    run.Add(
                        registration.Type,
                        row.Id,
                        "KEC0004",
                        FormattableString.Invariant(
                            $"Row {row.Id} carries a value for '{field.Name}' on type '{registration.TypeKey}', which is a derived localized text key. The key is derived from the type key, the content key and the field name, so there is nowhere to put an authored one."));
                }

                if (ContentTextKey.ExceedsBound(registration.TypeKey, row.Key.Utf8.Length, field.Name))
                {
                    run.Add(
                        registration.Type,
                        row.Id,
                        "KEC0030",
                        FormattableString.Invariant(
                            $"Row {row.Id} derives the localization key '{registration.TypeKey}.{row.Key}.{field.Name}', which is over the {ContentTextKey.MaxKeyLength} character bound. Three widest segments derive 194 characters, so the bound is reachable rather than theoretical."));
                }

                continue;
            }

            if (value.IsAbsent && field.Required && !row.IsRetired)
            {
                run.Add(
                    registration.Type,
                    row.Id,
                    "KEC0005",
                    FormattableString.Invariant(
                        $"Live row {row.Id} of type '{registration.TypeKey}' carries no value for '{field.Name}', which its schema marks required."));
            }
        }
    }

    /// <summary>
    /// <c>KEC0031</c>, the phase 1 guard, plus the four inheritance checks of spec 3.8.
    /// <para>
    /// <c>KEC0031</c> refuses any non-zero <c>parent_id</c> while the resolver is unimplemented, so the
    /// chain checks below never see a candidate a publish would accept. They are written anyway, from phase
    /// 1, because a row model that really does allow inheritance is the thing being proved: the chain cap is
    /// <c>KEC0032</c>, a parent of another content type is <c>KEC0033</c>, a parent that is not live is
    /// <c>KEC0034</c> and a cycle is <c>KEC0035</c>.
    /// </para>
    /// <para>
    /// <c>KEC0033</c> has no input in phase 1 and cannot fire: a row carries ONE parent id and no parent
    /// type, so the parent is read from the row's own type by construction and a cross-type parent cannot be
    /// expressed. It becomes reachable the day a parent reference carries a type of its own.
    /// </para>
    /// </summary>
    static void CheckInheritance(ContentValidationRun run, ContentTypeRegistration registration, ContentRow row)
    {
        if (row.ParentId == 0)
        {
            return;
        }

        run.Add(
            registration.Type,
            row.Id,
            "KEC0031",
            FormattableString.Invariant(
                $"Row {row.Id} of type '{registration.TypeKey}' names parent {row.ParentId}. Inheritance has no resolver yet, so a parent id is refused rather than silently ignored."));

        var seen = new HashSet<int> { row.Id };
        int parent = row.ParentId;
        for (int depth = 1; parent != 0; depth++)
        {
            if (depth > MaxParentChain)
            {
                run.Add(
                    registration.Type,
                    row.Id,
                    "KEC0032",
                    FormattableString.Invariant(
                        $"Row {row.Id} of type '{registration.TypeKey}' walks a parent chain deeper than {MaxParentChain}."));
                return;
            }

            if (!seen.Add(parent))
            {
                run.Add(
                    registration.Type,
                    row.Id,
                    "KEC0035",
                    FormattableString.Invariant(
                        $"Row {row.Id} of type '{registration.TypeKey}' is on a parent cycle through row {parent}."));
                return;
            }

            if (!run.Candidate.TryGetRow(registration.Type, parent, out ContentRow? ancestor)
                || run.Candidate.IsRetired(registration.Type, parent))
            {
                run.Add(
                    registration.Type,
                    row.Id,
                    "KEC0034",
                    FormattableString.Invariant(
                        $"Row {row.Id} of type '{registration.TypeKey}' names parent {parent}, which is not a live row of this version."));
                return;
            }

            parent = ancestor.ParentId;
        }
    }

    /// <summary>
    /// <c>KEC0020</c> and <c>KEC0021</c> on the engine <c>stat</c> type. The scale is a fixed power of ten
    /// and the stored integer is the value times the scale, which is what keeps every stat, every modifier
    /// and every displayed number an integer on both sides of the wire.
    /// </summary>
    static void CheckStatRanges(ContentValidationRun run)
    {
        if (!run.TryGetEngineType(EngineContentTypes.StatTypeKey, out ContentTypeRegistration? stat))
        {
            return;
        }

        int scaleIndex = ContentValidationRun.FieldIndex(stat.Schema, StatContentType.ScaleField);
        int minIndex = ContentValidationRun.FieldIndex(stat.Schema, StatContentType.MinField);
        int maxIndex = ContentValidationRun.FieldIndex(stat.Schema, StatContentType.MaxField);

        foreach (ContentRow row in run.Candidate.Rows(stat.Type))
        {
            ContentFieldValue scale = ContentValidationRun.Value(row, scaleIndex, ContentFieldKind.Int);
            if (!scale.IsAbsent && !IsPowerOfTen(scale.Number))
            {
                run.Add(
                    stat.Type,
                    row.Id,
                    "KEC0020",
                    FormattableString.Invariant(
                        $"Stat '{row.Key}' declares a scale of {scale.Number}. A scale is a power of ten and at least 1, because the stored integer is the value times the scale and there is no float anywhere in content."));
            }

            ContentFieldValue min = ContentValidationRun.Value(row, minIndex, ContentFieldKind.Int);
            ContentFieldValue max = ContentValidationRun.Value(row, maxIndex, ContentFieldKind.Int);
            if (!min.IsAbsent && !max.IsAbsent && min.Number > max.Number)
            {
                run.Add(
                    stat.Type,
                    row.Id,
                    "KEC0021",
                    FormattableString.Invariant(
                        $"Stat '{row.Key}' declares min {min.Number} above max {max.Number}. The pair is the inclusive clamp of the evaluation formula, so an inverted one clamps every value to nothing."));
            }
        }
    }

    /// <summary>
    /// <c>KEC0025</c> on the engine <c>item</c> type. A stack cap below 1 is not a stack at all, and a
    /// stackable row capped at 1 is the defect a consumer's admin form reaches today, so the pair is refused
    /// rather than interpreted.
    /// </summary>
    static void CheckStackShape(ContentValidationRun run)
    {
        if (!run.TryGetEngineType(EngineContentTypes.ItemTypeKey, out ContentTypeRegistration? item))
        {
            return;
        }

        int stackableIndex = ContentValidationRun.FieldIndex(item.Schema, ItemContentType.StackableField);
        int maxStackIndex = ContentValidationRun.FieldIndex(item.Schema, ItemContentType.MaxStackField);

        foreach (ContentRow row in run.Candidate.Rows(item.Type))
        {
            ContentFieldValue maxStack = ContentValidationRun.Value(row, maxStackIndex, ContentFieldKind.Int);
            if (maxStack.IsAbsent)
            {
                continue;
            }

            bool stackable = ContentValidationRun.Value(row, stackableIndex, ContentFieldKind.Bool).Number != 0;
            if (maxStack.Number < 1)
            {
                run.Add(
                    item.Type,
                    row.Id,
                    "KEC0025",
                    FormattableString.Invariant(
                        $"Item '{row.Key}' declares a max stack of {maxStack.Number}. One slot holds at least one of anything."));
            }
            else if (stackable && maxStack.Number == 1)
            {
                run.Add(
                    item.Type,
                    row.Id,
                    "KEC0025",
                    FormattableString.Invariant(
                        $"Item '{row.Key}' is stackable and caps its stack at 1, which is the same as not stacking. Say one or the other."));
            }
        }
    }

    /// <summary>True for 1, 10, 100 and the rest, which is the only shape a stat scale takes.</summary>
    static bool IsPowerOfTen(long value)
    {
        if (value < 1)
        {
            return false;
        }

        while (value % 10 == 0)
        {
            value /= 10;
        }

        return value == 1;
    }
}
