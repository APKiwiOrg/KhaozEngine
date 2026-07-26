using System;
using System.Collections.Generic;

namespace KhaozEngine.Stats;

/// <summary>
/// A dense, named-channel stat set: a fixed number of numeric channels, each with a base value plus a
/// stack of modifiers keyed by a stable <see cref="StatSourceId"/>. The engine owns the channels, the
/// fold, and the recompute, and never learns what a channel means: no stat identities, no balance
/// constants, no stacking / duration / expiry rules live here, that is entirely the game's concern.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fold, and it is the only one.</b> For a channel <c>c</c>:
/// <code>
/// Value(c) = (Base(c) + sum of Flat over all sources) * max(1 + sum of Percent over all sources, MinimumScale)
/// </code>
/// Every source that carries a modifier targeting <c>c</c> contributes its <see cref="StatModifier.Flat"/> to the
/// flat sum and its <see cref="StatModifier.Percent"/> to the percent sum. Flat modifiers apply before the percent
/// multiplier, never after.
/// </para>
/// <para>
/// <b>Insertion order is load-bearing.</b> Sources are held in insertion order (a plain list, linear lookup by
/// id), not a <see cref="Dictionary{TKey, TValue}"/>. Floating-point addition is not associative, so the order
/// the flat and percent sums are accumulated in can change the last bit of the result. A stable, reproducible
/// order is what makes "add a source, then remove it" return the exact bits the channel had before, and what
/// makes two runs that perform the same sequence of adds and removes agree on the same floats. A dictionary's
/// iteration order is not guaranteed stable across removals, so it could not offer this guarantee. Replacing a
/// source under an existing id keeps that source at its original position, and removing a source preserves
/// the relative order of the survivors.
/// </para>
/// <para>
/// <b>Recompute is lazy, per-channel, and always from scratch.</b> Each channel carries a dirty flag. A read
/// only re-sums that channel's flat and percent contributions across every source when the flag is set, then
/// caches the result. There is no running total that <see cref="AddSource"/> / <see cref="RemoveSource"/> nudges
/// in place: a running total accumulates floating-point error across add/remove cycles and can drift from the
/// value a from-scratch fold would produce, which is exactly the class of bug this kernel exists to prevent.
/// </para>
/// </remarks>
public sealed class StatSet
{
    private readonly struct SourceEntry
    {
        public SourceEntry(StatSourceId id, StatModifier[] modifiers)
        {
            Id = id;
            Modifiers = modifiers;
        }

        public StatSourceId Id { get; }
        public StatModifier[] Modifiers { get; }
    }

    private readonly float[] _bases;
    private readonly float[] _cache;
    private readonly bool[] _dirty;
    private readonly List<SourceEntry> _sources = new();

    /// <summary>The fixed number of channels this set holds. Valid channel indices are <c>[0, ChannelCount)</c>.</summary>
    public int ChannelCount { get; }

    /// <summary>
    /// The floor applied to the percent multiplier (<c>max(1 + sum of Percent, MinimumScale)</c>), so a stack of
    /// negative percent modifiers cannot invert a channel's sign. Pass <see cref="float.NegativeInfinity"/> at
    /// construction to disable the floor entirely and let the multiplier go arbitrarily negative.
    /// </summary>
    public float MinimumScale { get; }

    /// <summary>The number of distinct sources currently held (added, and not yet removed or cleared).</summary>
    public int SourceCount => _sources.Count;

    /// <summary>Creates a stat set with <paramref name="channelCount"/> channels, every base starting at 0.</summary>
    /// <param name="channelCount">The fixed number of channels. Must be non-negative.</param>
    /// <param name="minimumScale">
    /// The floor for the percent multiplier (see <see cref="MinimumScale"/>). Defaults to 0, which stops a
    /// negative-percent stack from inverting a channel's sign while still letting it reach exactly 0. Pass
    /// <see cref="float.NegativeInfinity"/> to disable the floor.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channelCount"/> is negative.</exception>
    public StatSet(int channelCount, float minimumScale = 0f)
    {
        if (channelCount < 0)
            throw new ArgumentOutOfRangeException(nameof(channelCount), channelCount, "Channel count must be non-negative.");

        ChannelCount = channelCount;
        MinimumScale = minimumScale;
        _bases = new float[channelCount];
        _cache = new float[channelCount];
        _dirty = new bool[channelCount];
        Array.Fill(_dirty, true); // no cached value yet: the first Value(c) read must recompute.
    }

    /// <summary>Sets the base value of a channel, before any modifier is folded in.</summary>
    /// <param name="channel">The channel index. Must be in <c>[0, ChannelCount)</c>.</param>
    /// <param name="value">The new base value.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is outside <c>[0, ChannelCount)</c>.</exception>
    public void SetBase(int channel, float value)
    {
        ValidateChannel(channel);
        _bases[channel] = value;
        _dirty[channel] = true;
    }

    /// <summary>Reads the base value of a channel, before any modifier is folded in. Starts at 0.</summary>
    /// <param name="channel">The channel index. Must be in <c>[0, ChannelCount)</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is outside <c>[0, ChannelCount)</c>.</exception>
    public float GetBase(int channel)
    {
        ValidateChannel(channel);
        return _bases[channel];
    }

    /// <summary>
    /// Adds, or replaces, the modifier stack held under <paramref name="id"/>. If a source with this id already
    /// holds modifiers, they are replaced outright, not appended to, and the source keeps its original position
    /// in the insertion order. Otherwise the source is appended at the end.
    /// </summary>
    /// <remarks>
    /// Every modifier's <see cref="StatModifier.Channel"/> is validated before any state is mutated: a span
    /// carrying one out-of-range channel throws and leaves the set completely untouched, so a caller never has to
    /// unwind a partially-applied source.
    /// </remarks>
    /// <param name="id">The stable source identity. Adding under an id that already exists replaces its modifiers.</param>
    /// <param name="modifiers">The modifiers this source contributes. Copied, the caller's span is not retained.</param>
    /// <exception cref="ArgumentOutOfRangeException">Some entry in <paramref name="modifiers"/> targets a channel outside <c>[0, ChannelCount)</c>.</exception>
    public void AddSource(StatSourceId id, ReadOnlySpan<StatModifier> modifiers)
    {
        for (int i = 0; i < modifiers.Length; i++)
        {
            int channel = modifiers[i].Channel;
            if (channel < 0 || channel >= ChannelCount)
                throw new ArgumentOutOfRangeException(nameof(modifiers), channel,
                    $"Modifier {i} targets channel {channel}, outside [0, {ChannelCount}).");
        }

        StatModifier[] copy = modifiers.ToArray();

        int existingIndex = IndexOfSource(id);
        if (existingIndex >= 0)
        {
            DirtyChannelsOf(_sources[existingIndex].Modifiers); // the replaced modifiers' contribution is removed
            _sources[existingIndex] = new SourceEntry(id, copy); // same slot: keeps its original insertion position
        }
        else
        {
            _sources.Add(new SourceEntry(id, copy));
        }

        DirtyChannelsOf(copy); // the new modifiers' contribution is added
    }

    /// <summary>
    /// Removes the source's modifiers, if it currently holds any. Removing an id that was never added, or was
    /// already removed, is a no-op.
    /// </summary>
    /// <param name="id">The source identity to remove.</param>
    /// <returns>True if a source with this id existed and was removed, false otherwise.</returns>
    public bool RemoveSource(StatSourceId id)
    {
        int index = IndexOfSource(id);
        if (index < 0)
            return false;

        DirtyChannelsOf(_sources[index].Modifiers);
        _sources.RemoveAt(index); // preserves the relative order of the surviving sources
        return true;
    }

    /// <summary>Drops every source. Base values are untouched.</summary>
    public void ClearSources()
    {
        _sources.Clear();
        Array.Fill(_dirty, true); // simplest correct invalidation: any channel may have lost a contribution.
    }

    /// <summary>
    /// Reads the folded value of a channel: <c>(Base(c) + sum of Flat) * max(1 + sum of Percent, MinimumScale)</c>
    /// over every modifier from every source targeting <paramref name="channel"/>. Recomputed from scratch when
    /// the channel is dirty (see the <see cref="StatSet"/> remarks), then cached until the next change that
    /// touches it.
    /// </summary>
    /// <param name="channel">The channel index. Must be in <c>[0, ChannelCount)</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is outside <c>[0, ChannelCount)</c>.</exception>
    public float Value(int channel)
    {
        ValidateChannel(channel);
        if (_dirty[channel])
        {
            _cache[channel] = Recompute(channel);
            _dirty[channel] = false;
        }
        return _cache[channel];
    }

    /// <summary>
    /// Writes <see cref="Value(int)"/> for every channel, in channel order, into <paramref name="destination"/>.
    /// Allocation-free: intended for a bulk read such as a server snapshot.
    /// </summary>
    /// <param name="destination">The span to write into. Must be at least <see cref="ChannelCount"/> long.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="ChannelCount"/>.</exception>
    public void CopyValuesTo(Span<float> destination)
    {
        if (destination.Length < ChannelCount)
            throw new ArgumentException(
                $"Destination span (length {destination.Length}) is shorter than ChannelCount ({ChannelCount}).",
                nameof(destination));

        for (int c = 0; c < ChannelCount; c++)
            destination[c] = Value(c);
    }

    // Sums flat and percent contributions fresh from every source, every time, never a running total that
    // AddSource/RemoveSource nudges incrementally. See the StatSet remarks: a running total drifts from the
    // from-scratch fold over repeated add/remove cycles, which is the bug class this kernel exists to prevent.
    private float Recompute(int channel)
    {
        float flatSum = 0f;
        float percentSum = 0f;

        for (int i = 0; i < _sources.Count; i++)
        {
            StatModifier[] modifiers = _sources[i].Modifiers;
            for (int j = 0; j < modifiers.Length; j++)
            {
                if (modifiers[j].Channel != channel)
                    continue;
                flatSum += modifiers[j].Flat;
                percentSum += modifiers[j].Percent;
            }
        }

        float scale = MathF.Max(1f + percentSum, MinimumScale);
        return (_bases[channel] + flatSum) * scale;
    }

    private int IndexOfSource(StatSourceId id)
    {
        for (int i = 0; i < _sources.Count; i++)
            if (_sources[i].Id == id)
                return i;
        return -1;
    }

    private void DirtyChannelsOf(StatModifier[] modifiers)
    {
        for (int i = 0; i < modifiers.Length; i++)
            _dirty[modifiers[i].Channel] = true;
    }

    private void ValidateChannel(int channel)
    {
        if (channel < 0 || channel >= ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel must be in [0, {ChannelCount}).");
    }
}
