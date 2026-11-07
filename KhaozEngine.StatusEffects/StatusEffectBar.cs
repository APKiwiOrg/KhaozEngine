using System;
using System.Collections.Generic;

namespace KhaozEngine.StatusEffects;

/// <summary>Builds presentation rows from replicated effect windows and an estimated server tick.</summary>
public static class StatusEffectBar
{
    /// <summary>Appends known, unexpired entries in input order without clearing the destination.</summary>
    /// <remarks>
    /// Remaining ticks retain the fractional part of <paramref name="nowTick"/>. Fractions are clamped
    /// to [0, 1], with zero for a zero-length window. The caller selects which entries to present.
    /// </remarks>
    public static void Build(ReadOnlySpan<StatusEffectEntry> entries, IStatusEffectCatalog catalog,
        double nowTick, List<StatusEffectBarItem> into)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(into);
        foreach (var entry in entries)
        {
            if (entry.EndTick <= nowTick || !catalog.TryGet(entry.Effect, out var definition))
                continue;

            double remaining = entry.EndTick - nowTick;
            // Subtract before converting so nearby large ticks retain their duration without overflow.
            double duration = (double)((Int128)entry.EndTick - entry.StartTick);
            float fraction = duration == 0 ? 0 : (float)Math.Clamp(remaining / duration, 0, 1);
            into.Add(new(entry.Effect, definition.IconKey, definition.Polarity, entry.Source, entry.Stacks,
                remaining, fraction));
        }
    }
}
