using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The candidate's <c>game_tuning</c> rows as a lookup, read once per sweep rather than once per rule.
/// </summary>
/// <remarks>
/// A duplicate knob name OVERWRITES rather than throwing, because two rows under one key is already the
/// engine's <c>KEC0002</c> and a validator that threw would be reported as a defective validator instead
/// of letting the real finding through.
/// </remarks>
sealed class GameTuningKnobs
{
    readonly Dictionary<string, long> _byName = new(StringComparer.Ordinal);

    internal GameTuningKnobs(IContentSnapshot candidate)
    {
        foreach (ContentRow row in candidate.Rows(new ContentTypeId(GameContentTypeIds.GameTuning)))
        {
            if (row.IsRetired || row.Fields.Count != GameTuningContentType.FieldCount)
            {
                continue;
            }

            Authored = true;
            ContentFieldValue value = row.Fields[GameTuningContentType.ValueIndex];
            if (!value.IsAbsent)
            {
                _byName[row.Key.ToString()] = value.Number;
            }
        }
    }

    /// <summary>Whether the candidate carries a live tuning row at all, which gates the set rule.</summary>
    internal bool Authored { get; }

    /// <summary>One knob's stored number, at <see cref="GameTuningContentType.ValueScale"/>.</summary>
    internal bool TryGet(string knob, out long scaled) => _byName.TryGetValue(knob, out scaled);

    /// <summary>A knob's stored number as the value an author typed, for a message.</summary>
    internal static decimal Whole(long scaled) => scaled / (decimal)GameTuningContentType.ValueScale;
}
