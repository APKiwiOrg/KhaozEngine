using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// <see cref="InstanceStatLines"/>' output cursor, in its own file because it is a cohesive type rather
/// than a paragraph of the builder: it owns the per source arithmetic and the three caller spans, and
/// nothing outside it touches either. The holder is a partial only for that reason, and no member moved.
/// </summary>
public sealed partial class InstanceStatLines
{
    /// <summary>
    /// The output cursor over the three caller spans, and the ONE place the per source arithmetic lives.
    /// <para>
    /// <see cref="Open"/> marks where a source's lines and tags begin, <see cref="Write"/> appends one
    /// line with its scope REBASED onto that mark, and <see cref="Close"/> seals the run or drops it when
    /// it stayed empty. A tier with no live row leaves the run empty and closes to nothing, which is why a
    /// stored affix naming content a later version dropped costs a source rather than a refusal.
    /// </para>
    /// <para>
    /// Every method answers false rather than throwing when a span runs out, and a build that gets a false
    /// reports zero of everything: a caller reading a partial answer as a whole one is how an item would
    /// silently lose an affix.
    /// </para>
    /// </summary>
    ref struct Writer
    {
        readonly Span<StatModifierLine> _lines;
        readonly Span<int> _tags;
        readonly Span<InstanceStatSource> _sources;
        int _lineOrigin;
        int _tagOrigin;

        internal Writer(Span<StatModifierLine> lines, Span<int> tags, Span<InstanceStatSource> sources)
        {
            _lines = lines;
            _tags = tags;
            _sources = sources;
        }

        /// <summary>The lines written so far, across every closed source.</summary>
        internal int LineCount { get; private set; }

        /// <summary>The tags written so far.</summary>
        internal int TagCount { get; private set; }

        /// <summary>The sources closed so far.</summary>
        internal int SourceCount { get; private set; }

        /// <summary>Marks where the next source's lines and tags start.</summary>
        internal void Open()
        {
            _lineOrigin = LineCount;
            _tagOrigin = TagCount;
        }

        /// <summary>Appends one line, copying its scope in and rebasing the scope start onto the source.</summary>
        internal bool Write(
            int statId,
            StatCombineKind combine,
            int value,
            ReadOnlySpan<int> scope,
            int conditionId)
        {
            if (LineCount == _lines.Length || _tags.Length - TagCount < scope.Length)
            {
                return false;
            }

            int scopeStart = TagCount - _tagOrigin;
            scope.CopyTo(_tags[TagCount..]);
            TagCount += scope.Length;
            _lines[LineCount++] = new StatModifierLine(
                statId,
                combine,
                value,
                scopeStart,
                scope.Length,
                conditionId);
            return true;
        }

        /// <summary>Seals the open run under one key, or drops it when no line landed in it.</summary>
        internal bool Close(in StatSourceKey key)
        {
            if (LineCount == _lineOrigin)
            {
                return true;
            }

            if (SourceCount == _sources.Length)
            {
                return false;
            }

            _sources[SourceCount++] = new InstanceStatSource(
                key,
                _lineOrigin,
                LineCount - _lineOrigin,
                _tagOrigin,
                TagCount - _tagOrigin);
            return true;
        }
    }
}
