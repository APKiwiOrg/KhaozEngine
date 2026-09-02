using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D
{
    /// <summary>Horizontal alignment of text within a region.</summary>
    public enum TextAlign { Left, Center, Right }

    /// <summary>
    /// Pure text-layout helpers (word-wrap + alignment) over an <see cref="ITextMeasurer"/>, plus draw
    /// overloads that take a <see cref="SpriteBatch"/> + <see cref="SpriteFont"/>. The layout math is
    /// device-free and headless-testable. Ported from the 4.x <c>TextHelper</c> (which was MonoGame-bound).
    /// </summary>
    public static class TextLayout
    {
        // --- pure layout (headless-testable) ---

        /// <summary>The X (pixels) at which a line of <paramref name="text"/> starts so it aligns within
        /// [<paramref name="left"/>, <paramref name="left"/> + <paramref name="width"/>]. The measured width is
        /// multiplied by <paramref name="scale"/> so alignment stays correct for text drawn at that scale
        /// (<c>scale = 1</c> is the unscaled path).</summary>
        public static float AlignedX(ITextMeasurer font, string text, float left, float width, TextAlign align, float scale = 1f)
        {
            float textW = font.Measure(text).X * scale;
            return align switch
            {
                TextAlign.Center => left + (width - textW) * 0.5f,
                TextAlign.Right => left + width - textW,
                _ => left,
            };
        }

        /// <summary>Word-wraps <paramref name="text"/> so each line fits within <paramref name="maxWidth"/>
        /// pixels, breaking on spaces and at explicit line breaks. By default a single word wider than the limit stays on its own line
        /// (never dropped); set <paramref name="hardBreak"/> to instead slice such a word at character
        /// boundaries so every returned line fits within <paramref name="maxWidth"/> (a word narrower than one
        /// character still yields at least one character per line, so it always makes progress).
        /// <para>
        /// By default a run of interior spaces is COLLAPSED to a single space on every output line (fine for
        /// engine-authored labels and tooltips). Set <paramref name="preserveSpaceRuns"/> to keep USER-authored
        /// spacing intact: a space run is still ONE break opportunity, but when no break is taken there the run is
        /// re-emitted verbatim (a break taken at the run still consumes it, so a fresh line never carries leading
        /// spaces). This is the mode for wrapping player-typed content (chat) where collapsing spaces would silently
        /// rewrite the text.
        /// </para>
        /// <para>
        /// A <c>\n</c> in <paramref name="text"/> is an explicit line break in EITHER mode, and a <c>\r\n</c> pair
        /// is one break rather than two (a lone <c>\r</c> is not a break, it stays an ordinary character). N breaks
        /// therefore yield N+1 lines before the width has any say, so consecutive breaks keep their empty lines
        /// ("a\n\nb" is three lines) and a text ending on a break keeps its empty last line ("a\n" is two). Spaces
        /// touching a break are consumed with it, the same way a width-forced break consumes the run it lands on,
        /// so no output line carries leading or trailing space from the break. Height accounting follows for free,
        /// since <see cref="MeasureWrappedHeight"/> counts the lines this returns.
        /// </para>
        /// <para>
        /// Memoized: the result is a pure function of (<paramref name="font"/> identity, <paramref name="text"/>,
        /// <paramref name="maxWidth"/>, <paramref name="hardBreak"/>, <paramref name="preserveSpaceRuns"/>), so a
        /// caller that recomputes the same wrap every frame (a static label, an unchanged tooltip) hits a bounded LRU
        /// cache instead of re-running the wrap algorithm. The mode is part of the cache key, so the same string
        /// wrapped both collapsed and preserved never returns one poisoning the other. <see cref="DrawWrapped"/> folds
        /// its <c>scale</c> parameter into the effective <paramref name="maxWidth"/> it passes here
        /// (<c>maxWidth / scale</c>), so scale is already part of the cache key via that effective width - two
        /// different (width, scale) pairs that resolve to the same effective width correctly share one cache entry,
        /// since <see cref="Wrap"/>'s output depends only on that effective width. The cache is bounded PER
        /// MEASURER (oldest-unused entries evicted) so a session that streams many distinct texts through one font
        /// (chat, procedurally generated labels) cannot grow it without limit, and it hangs off the measurer
        /// weakly, so a font nobody references any more is collected with its entries instead of being pinned
        /// here until the bound ages it out. The returned list is always a fresh copy, so a caller
        /// mutating it can never corrupt the cache. Concurrent callers are safe, including off the render thread:
        /// the wrap itself runs unlocked, and two threads that miss on the same key both compute it, then the
        /// second to finish adopts the entry the first inserted instead of adding a duplicate.
        /// </para></summary>
        public static List<string> Wrap(ITextMeasurer font, string text, float maxWidth, bool hardBreak = false, bool preserveSpaceRuns = false)
        {
            var key = new WrapKey(text, maxWidth, hardBreak, preserveSpaceRuns);
            lock (WrapCacheLock)
            {
                if (TryGetCachedWrap(font, key, out List<string>? cachedHit))
                    return new List<string>(cachedHit);
            }

            List<string> lines = ComputeWrap(font, text, maxWidth, hardBreak, preserveSpaceRuns);

            lock (WrapCacheLock)
            {
                // Re-check before inserting. The cache is UNLOCKED across ComputeWrap (see WrapCacheLock below
                // for why it has to be), so another thread can have computed and inserted this very key while we
                // were measuring. Inserting a second node for it would leave the first one in the LRU list with
                // no index row pointing at it, an orphan that grows the list past capacity and, when it reaches
                // the tail, evicts the index row belonging to the OTHER thread's still-live node (#87). Wrap is a
                // pure function of the key, so the two results are equal and the loser simply adopts the
                // winner's entry rather than fighting over it.
                if (TryGetCachedWrap(font, key, out List<string>? winner))
                    return new List<string>(winner);

                CacheWrap(font, key, lines);
            }
            return new List<string>(lines);
        }

        // The actual word-wrap computation (cache-miss path). Builds each candidate line in one scratch buffer
        // instead of concatenating a new string per word: the reconstructed line can never exceed the scan
        // cursor's position in the source text (every appended word plus its separating space was already read
        // from `text`, and an explicit line break is consumed without ever entering the buffer), so one upfront
        // buffer sized to `text.Length` never needs to grow. A candidate that turns
        // out not to fit costs no allocation - it is just overwritten in place by the next line's content - only
        // a line actually kept costs the one unavoidable allocation: the `string` for that output line.
        static List<string> ComputeWrap(ITextMeasurer font, string text, float maxWidth, bool hardBreak, bool preserveSpaceRuns)
        {
            var lines = new List<string>();
            if (text.Length == 0) return lines;

            ReadOnlySpan<char> span = text.AsSpan();
            Span<char> buf = text.Length <= 512 ? stackalloc char[text.Length] : new char[text.Length];
            int bufLen = 0;
            int pos = 0;
            bool lineOpen = false;   // an explicit break was taken, so a line is pending even with nothing after it

            while (pos < span.Length)
            {
                // The separator run ahead of the next word: spaces and explicit line breaks, in any mix. A run
                // holding at least one break IS a break, and the spaces around it are consumed with it, exactly as
                // a width-forced break consumes its run today. The space count therefore only matters when the run
                // holds no break at all.
                int spaceStart = pos;
                int breaks = 0;
                while (pos < span.Length)
                {
                    if (span[pos] == ' ') { pos++; continue; }
                    if (!IsBreakAt(span, pos, out int breakLen)) break;
                    breaks++;
                    pos += breakLen;
                }
                int spaceRun = breaks == 0 ? pos - spaceStart : 0;   // how many, for the preserve-spaces mode

                if (breaks > 0)
                {
                    // N breaks end the line being built and open N more. The lines between two consecutive breaks
                    // stay in the output as empty lines: a blank line between paragraphs is authored content, and
                    // MeasureWrappedHeight (which just counts these) has to reserve its height.
                    lines.Add(new string(buf[..bufLen]));
                    for (int b = 1; b < breaks; b++) lines.Add(string.Empty);
                    bufLen = 0;
                    lineOpen = true;
                }

                if (pos >= span.Length) break;
                int wordStart = pos;
                while (pos < span.Length && span[pos] != ' ' && !IsBreakAt(span, pos, out _)) pos++;
                ReadOnlySpan<char> word = span.Slice(wordStart, pos - wordStart);

                // The separator emitted before this word when it packs onto the current line: none at a line start,
                // otherwise a single space (default, collapsing the run) or the run verbatim (preserve mode). Taking a
                // BREAK here still consumes the run (the fresh line below starts with just the word), matching the
                // default break behaviour. The reconstructed line is a contiguous slice of the source either way, so
                // the one text.Length buffer never needs to grow.
                int sep = bufLen == 0 ? 0 : (preserveSpaceRuns ? spaceRun : 1);
                int candidateLen = bufLen + sep + word.Length;
                int w = bufLen;
                for (int s = 0; s < sep; s++) buf[w++] = ' ';
                word.CopyTo(buf.Slice(w));

                if (bufLen == 0 || font.Measure(buf[..candidateLen]).X <= maxWidth)
                {
                    bufLen = candidateLen;   // keep the candidate
                }
                else
                {
                    lines.Add(new string(buf[..bufLen]));   // commit the previous line
                    word.CopyTo(buf);                        // start a fresh line with just this word
                    bufLen = word.Length;
                }

                // The buffered line may now be a lone word wider than the limit (a fresh line start, or the very
                // first word). With hardBreak, slice it: emit the full chunks and keep the trailing remainder in
                // the buffer so following words can still pack onto it.
                if (hardBreak && bufLen > 0 && buf[..bufLen].IndexOf(' ') < 0 && font.Measure(buf[..bufLen]).X > maxWidth)
                {
                    string overflowing = new string(buf[..bufLen]);
                    List<string> chunks = HardBreak(font, overflowing, maxWidth);
                    for (int c = 0; c < chunks.Count - 1; c++) lines.Add(chunks[c]);
                    string remainder = chunks[^1];
                    remainder.AsSpan().CopyTo(buf);
                    bufLen = remainder.Length;
                }
            }

            // The trailing empty line is real when the text ended on a break ("a\n" is two lines, the second
            // empty), which is what lineOpen carries past the loop.
            if (bufLen > 0 || lineOpen) lines.Add(new string(buf[..bufLen]));
            return lines;
        }

        // An explicit line break at `i`: '\n' on its own, or a "\r\n" pair counted as ONE break, with `width` the
        // characters to step past. A lone '\r' is deliberately NOT a break: it stays an ordinary character and
        // measures as one, the same as every other control character the wrap does not interpret.
        static bool IsBreakAt(ReadOnlySpan<char> span, int i, out int width)
        {
            if (span[i] == '\n') { width = 1; return true; }
            if (span[i] == '\r' && i + 1 < span.Length && span[i + 1] == '\n') { width = 2; return true; }
            width = 0;
            return false;
        }

        // Slice a single (space-free) word into the fewest chunks that each fit within maxWidth, growing each
        // chunk greedily and always taking at least one character so a word narrower than a glyph still advances.
        // Only reached via hardBreak (a rarer, opt-in path), so the Substring allocations here are left as-is.
        static List<string> HardBreak(ITextMeasurer font, string word, float maxWidth)
        {
            var chunks = new List<string>();
            int start = 0;
            while (start < word.Length)
            {
                int len = 1;
                while (start + len < word.Length && font.Measure(word.AsSpan(start, len + 1)).X <= maxWidth)
                    len++;
                chunks.Add(word.Substring(start, len));
                start += len;
            }
            return chunks;
        }

        /// <summary>Total height (pixels) of <paramref name="text"/> word-wrapped to <paramref name="maxWidth"/>.
        /// <paramref name="preserveSpaceRuns"/> forwards to <see cref="Wrap"/> (it can change the line count when a
        /// preserved run pushes a wrap the collapsed form would not have).</summary>
        public static float MeasureWrappedHeight(ITextMeasurer font, string text, float maxWidth, bool preserveSpaceRuns = false) =>
            Wrap(font, text, maxWidth, preserveSpaceRuns: preserveSpaceRuns).Count * font.LineHeight;

        // -- Wrap()'s per-measurer bounded LRU memo cache --

        // The text/width/mode that fully determine Wrap's output FOR ONE measurer. The measurer itself is
        // deliberately not a field here: it is the ConditionalWeakTable key below instead, so a font nobody
        // references any more can be collected with its entries rather than sitting in a process-static
        // dictionary, glyph table and all, until 256 later wraps age it out of the LRU (#767). Font identity is
        // still part of the lookup (reference equality, since ITextMeasurer has no overridden Equals), it is just
        // carried by the table rather than by the key.
        readonly record struct WrapKey(string Text, float MaxWidth, bool HardBreak, bool PreserveSpaceRuns);

        // One bounded LRU per measurer. Holds NO reference back to the measurer it hangs off, directly or through
        // a key: a ConditionalWeakTable value that can reach its own key keeps the entry alive forever, which is
        // the exact leak this shape exists to close.
        sealed class MeasurerWrapCache
        {
            public readonly Dictionary<WrapKey, LinkedListNode<(WrapKey Key, List<string> Lines)>> Index = new();
            public readonly LinkedList<(WrapKey Key, List<string> Lines)> Lru = new();
        }

        // Not a hot per-frame allocation path (a cache hit/miss check, not the wrap itself), and Wrap() may be
        // called from tests/tools off the render thread, so guard the shared cache with a plain lock rather than
        // assuming single-threaded callers.
        //
        // The lock covers the lookup and the insert, never the computation between them. ComputeWrap calls
        // ITextMeasurer.Measure once per candidate line, which is CONSUMER code of unbounded cost: holding a
        // process-wide static lock across it would serialize every wrap in the process behind the slowest
        // measurer, and would invite a lock-order inversion the moment a measurer takes a lock of its own. So the
        // gap stays, and Wrap closes it by re-checking the cache under the second lock instead (#87).
        static readonly object WrapCacheLock = new();

        // PER MEASURER, not process-wide. The bound is there so one font streaming distinct texts (chat,
        // procedurally generated labels) cannot grow its cache without limit, and that property is exactly what a
        // per-measurer cap keeps. The process-wide total is now this times the number of LIVE measurers, which a
        // game holds a handful of, and a measurer that dies subtracts its own share.
        const int WrapCacheCapacity = 256;

        // The table holds the measurer WEAKLY, so nothing here is a reason for a disposed font to stay reachable.
        static readonly ConditionalWeakTable<ITextMeasurer, MeasurerWrapCache> WrapCaches = new();

        // Caller must hold WrapCacheLock.
        static bool TryGetCachedWrap(ITextMeasurer font, WrapKey key, [NotNullWhen(true)] out List<string>? lines)
        {
            if (WrapCaches.TryGetValue(font, out MeasurerWrapCache? cache) && cache.Index.TryGetValue(key, out var node))
            {
                cache.Lru.Remove(node);
                cache.Lru.AddFirst(node);   // most-recently-used
                lines = node.Value.Lines;
                return true;
            }
            lines = null;
            return false;
        }

        // Caller must hold WrapCacheLock AND must have re-checked, under that same lock acquisition, that the key
        // is absent: this inserts unconditionally, so a second insert for a live key orphans its first node (#87).
        static void CacheWrap(ITextMeasurer font, WrapKey key, List<string> lines)
        {
            MeasurerWrapCache cache = WrapCaches.GetValue(font, static _ => new MeasurerWrapCache());
            var node = new LinkedListNode<(WrapKey Key, List<string> Lines)>((key, lines));
            cache.Lru.AddFirst(node);
            cache.Index[key] = node;
            if (cache.Index.Count > WrapCacheCapacity)
            {
                LinkedListNode<(WrapKey Key, List<string> Lines)> lru = cache.Lru.Last!;
                cache.Lru.RemoveLast();
                cache.Index.Remove(lru.Value.Key);
            }
        }

        // Test hook for #87: the cache's structural invariant is exactly one LRU node per index entry. Every
        // mutation above runs under WrapCacheLock and moves both counts together, so a reader taking the same
        // lock never sees a torn intermediate state and the two counts are equal at every lock boundary. A racing
        // double insert used to break that by leaving a node in the list that no index row pointed at.
        //
        // Scoped to ONE measurer's cache since #767, which is the cache a race happens in anyway. It deliberately
        // does not walk the whole table: enumerating a ConditionalWeakTable hands out its keys, which would root
        // every live measurer for the duration and could keep a concurrently running lifetime test's font alive.
        // Zeroes for a measurer that has never been wrapped with.
        internal static (int IndexCount, int LruCount) WrapCacheCounts(ITextMeasurer font)
        {
            lock (WrapCacheLock)
            {
                return WrapCaches.TryGetValue(font, out MeasurerWrapCache? cache)
                    ? (cache.Index.Count, cache.Lru.Count)
                    : (0, 0);
            }
        }

        // --- drawing (needs the GPU-backed SpriteFont) ---

        /// <summary>Draws one line of <paramref name="text"/> horizontally aligned within
        /// [<paramref name="left"/>, <paramref name="left"/> + <paramref name="width"/>] at <paramref name="y"/>,
        /// uniformly scaled by <paramref name="scale"/> (<c>scale = 1</c> is the unscaled path).
        /// Positions are pixel-snapped to avoid sub-pixel blur.</summary>
        public static void DrawAligned(SpriteBatch batch, SpriteFont font, string text,
            float left, float width, float y, TextAlign align, Color color, float scale = 1f)
        {
            float x = MathF.Floor(AlignedX(font, text, left, width, align, scale));
            batch.DrawString(font, text, new Vector2(x, MathF.Floor(y)), color, scale);
        }

        /// <summary>Draws <paramref name="text"/> word-wrapped to <paramref name="maxWidth"/>, each line aligned
        /// within that width, starting at <paramref name="topLeft"/> and scaled by <paramref name="scale"/>
        /// (<c>scale = 1</c> is the unscaled path). Wrapping and line advance both account for the scale so the
        /// scaled lines fill <paramref name="maxWidth"/>. <paramref name="preserveSpaceRuns"/> forwards to
        /// <see cref="Wrap"/> for wrapping user-authored content without collapsing interior spacing. Returns the
        /// total height drawn.</summary>
        public static float DrawWrapped(SpriteBatch batch, SpriteFont font, string text,
            Vector2 topLeft, float maxWidth, TextAlign align, Color color, float scale = 1f, bool preserveSpaceRuns = false)
        {
            float y = topLeft.Y;
            foreach (string line in Wrap(font, text, maxWidth / scale, preserveSpaceRuns: preserveSpaceRuns))
            {
                DrawAligned(batch, font, line, topLeft.X, maxWidth, y, align, color, scale);
                y += font.LineHeight * scale;
            }
            return y - topLeft.Y;
        }
    }
}
