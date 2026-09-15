using System;
using System.Collections.Generic;

namespace KhaozEngine.Benchmarks.Items;

internal enum StatCombineKind : byte
{
    Flat = 1,
    Increased = 2,
    More = 3,
}

internal readonly record struct StatModifierLine(
    int StatId,
    StatCombineKind Combine,
    int Value,
    int TagScopeStart,
    int TagScopeLength,
    int ConditionId);

internal readonly record struct StatSourceKey(byte SourceKind, int Ordinal, long InstanceId);

internal readonly record struct StatContext(ReadOnlyMemory<int> Tags, int ConditionMask);

/// <summary>
/// Contracts 13.2's formula through spec 11.6's eight steps, in integer arithmetic with FLOOR division
/// at every divide, and a per-stat inverted index so a read walks only the lines that touch that stat
/// (11.5). Nothing on the read path allocates: every working array is built when a source is added.
/// </summary>
internal sealed class ContentStatEvaluator
{
    private readonly List<StatModifierLine> lines = new();
    private readonly List<int> scopeTags = new();
    private readonly Dictionary<StatSourceKey, int> sources = new();
    private readonly int[][] statIndex;
    private readonly int[] statIndexCount;
    private readonly int[] statBase;
    private readonly int[] statMinimum;
    private readonly int[] statMaximum;
    private readonly int[][] statTags;
    private readonly int[] cachedValue;
    private readonly bool[] cachedValid;

    internal ContentStatEvaluator(int statCount)
    {
        statIndex = new int[statCount + 1][];
        statIndexCount = new int[statCount + 1];
        statBase = new int[statCount + 1];
        statMinimum = new int[statCount + 1];
        statMaximum = new int[statCount + 1];
        statTags = new int[statCount + 1][];
        cachedValue = new int[statCount + 1];
        cachedValid = new bool[statCount + 1];
        for (int stat = 0; stat <= statCount; stat++)
        {
            statIndex[stat] = new int[8];
            statMinimum[stat] = -1_000_000;
            statMaximum[stat] = 1_000_000;
            statTags[stat] = Array.Empty<int>();
        }
    }

    internal int LineCount => lines.Count;

    internal int StatLineCount(int statId) => statIndexCount[statId];

    /// <summary>
    /// Spec 11.5: a source changes on exactly five events, and a read recomputes only when the stat is
    /// dirty. Nothing else dirties anything, and in particular a tick does not.
    /// </summary>
    internal void MarkDirty() => Array.Clear(cachedValid);

    internal int Value(int statId, in StatContext context)
    {
        if (cachedValid[statId]) return cachedValue[statId];
        int value = Recompute(statId, in context);
        cachedValue[statId] = value;
        cachedValid[statId] = true;
        return value;
    }

    internal void SetBase(int statId, int scaledValue) => statBase[statId] = scaledValue;

    internal void SetStatTags(int statId, int[] tags) => statTags[statId] = tags;

    internal void AddSource(in StatSourceKey key, ReadOnlySpan<StatModifierLine> sourceLines, ReadOnlySpan<int> tags)
    {
        Array.Clear(cachedValid);
        sources[key] = lines.Count;
        int tagStart = scopeTags.Count;
        foreach (int tag in tags) scopeTags.Add(tag);
        foreach (StatModifierLine line in sourceLines)
        {
            int lineIndex = lines.Count;
            lines.Add(line with { TagScopeStart = tagStart + line.TagScopeStart });
            int stat = line.StatId;
            if (statIndexCount[stat] == statIndex[stat].Length) Array.Resize(ref statIndex[stat], statIndex[stat].Length * 2);
            statIndex[stat][statIndexCount[stat]++] = lineIndex;
        }
    }

    internal int Recompute(int statId, in StatContext context)
    {
        int[] index = statIndex[statId];
        int count = statIndexCount[statId];
        ReadOnlySpan<StatModifierLine> all = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lines);
        ReadOnlySpan<int> scope = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(scopeTags);
        ReadOnlySpan<int> contextTags = context.Tags.Span;
        ReadOnlySpan<int> ownTags = statTags[statId];

        long flat = statBase[statId];
        long increased = 10_000;
        for (int position = 0; position < count; position++)
        {
            StatModifierLine line = all[index[position]];
            if (!Applies(in line, scope, contextTags, ownTags, context.ConditionMask)) continue;
            if (line.Combine == StatCombineKind.Flat) flat += line.Value;
            else if (line.Combine == StatCombineKind.Increased) increased += line.Value;
        }

        long value = FloorDiv((flat * increased) + 5_000, 10_000);
        for (int position = 0; position < count; position++)
        {
            StatModifierLine line = all[index[position]];
            if (line.Combine != StatCombineKind.More) continue;
            if (!Applies(in line, scope, contextTags, ownTags, context.ConditionMask)) continue;
            value = FloorDiv((value * (10_000 + line.Value)) + 5_000, 10_000);
        }

        if (value < statMinimum[statId]) return statMinimum[statId];
        if (value > statMaximum[statId]) return statMaximum[statId];
        return (int)value;
    }

    private static bool Applies(
        in StatModifierLine line,
        ReadOnlySpan<int> scope,
        ReadOnlySpan<int> contextTags,
        ReadOnlySpan<int> ownTags,
        int conditionMask)
    {
        if (line.ConditionId != 0 && (conditionMask & (1 << (line.ConditionId & 31))) == 0) return false;
        for (int position = 0; position < line.TagScopeLength; position++)
        {
            int required = scope[line.TagScopeStart + position];
            if (!Contains(contextTags, required) && !Contains(ownTags, required)) return false;
        }

        return true;
    }

    private static bool Contains(ReadOnlySpan<int> values, int value)
    {
        for (int position = 0; position < values.Length; position++)
            if (values[position] == value) return true;
        return false;
    }

    /// <summary>
    /// Floor division, spec 11.6. C# <c>/</c> truncates toward zero, which rounds a debuff differently
    /// from a buff of the same size: <c>(-9000) / 10000</c> is 0 where the floor is -1.
    /// </summary>
    internal static long FloorDiv(long numerator, long denominator)
    {
        long quotient = Math.DivRem(numerator, denominator, out long remainder);
        if (remainder != 0 && ((remainder < 0) != (denominator < 0))) quotient--;
        return quotient;
    }
}
