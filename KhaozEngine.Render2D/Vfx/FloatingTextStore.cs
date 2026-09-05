using System;
using System.Numerics;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// The live set of anchored floating text: add a line, age the set once a frame, and hand the whole thing to
    /// <see cref="FloatingTextRenderer"/>. Pure and GPU-free, so a headless test can drive the entire lifecycle.
    /// <para>ENTRIES ARE HELD OLDEST FIRST and stay that way, because every rule here is an age rule: expiry, the
    /// per-anchor cap's eviction, and the stack step all mean "the oldest one". Removal preserves that order rather
    /// than swapping the last entry into the hole, which would cost an anchor's column its reading order for the sake
    /// of one array write.</para>
    /// <para>ALLOCATION-LIGHT rather than allocation-free: the backing array doubles when it fills, and nothing else
    /// here allocates at all. A store whose capacity covers its busiest frame never allocates again, so
    /// <see cref="Age"/> on a steady state is free. Size it for the burst, not the average.</para>
    /// <para>ONE STORE PER SPACE, not per kind of line. The style is per entry, so experience drops, damage numbers
    /// and gameplay lines share one store and one draw call ordering. What must NOT share a store is a different
    /// coordinate space, which is what <see cref="FloatingBannerStore"/> is for.</para>
    /// </summary>
    public sealed class FloatingTextStore
    {
        FloatingText[] entries;
        int count;

        /// <summary>A store whose backing array starts at <paramref name="capacity"/> entries and doubles from
        /// there. Pick the busiest frame's live count, so a burst never reallocates mid-fight.</summary>
        /// <param name="capacity">Initial capacity. Below one it is treated as one.</param>
        public FloatingTextStore(int capacity = 32) => entries = new FloatingText[Math.Max(1, capacity)];

        /// <summary>How many entries are live right now.</summary>
        public int Count => count;

        /// <summary>How many entries the backing array holds before it has to grow.</summary>
        public int Capacity => entries.Length;

        /// <summary>Every live entry, oldest first, as a view over the backing array. Walk it to draw without
        /// allocating an enumerator. Invalidated by the next <see cref="Add"/>, <see cref="Age"/> or clear, so a
        /// caller reads it inside its own frame rather than keeping it.</summary>
        public ReadOnlySpan<FloatingText> Live => entries.AsSpan(0, count);

        /// <summary>
        /// Adds one line at age zero, pinned to <paramref name="anchorId"/>.
        /// <para>The per-anchor cap is applied BEFORE the add, by dropping that anchor's oldest entries until there
        /// is room, so the cap is a hard ceiling on what one anchor can ever hold rather than a ceiling it exceeds
        /// for a frame. The new entry's <see cref="FloatingText.StackIndex"/> is then the number of that anchor's
        /// entries still live, so a burst arriving on one frame reads as a column with the oldest highest.</para>
        /// <para>A null or empty text is refused rather than stored, because an entry nobody can read still holds a
        /// slot against the cap and still evicts a line somebody could.</para>
        /// </summary>
        /// <param name="text">The already-localized line.</param>
        /// <param name="anchorId">The thing it is pinned to.</param>
        /// <param name="offset">Design-space pixels from the anchor's point at birth.</param>
        /// <param name="style">How it looks and dies.</param>
        public void Add(string text, long anchorId, Vector2 offset, in FloatingTextStyle style)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (style.MaxPerAnchor > 0)
                while (CountFor(anchorId) >= style.MaxPerAnchor)
                    RemoveAt(IndexOfOldestFor(anchorId));

            // Read BEFORE the entry lands, so it is the number of siblings already there rather than including
            // itself, which is what makes the first of a burst take no step at all.
            int stackIndex = CountFor(anchorId);
            if (count == entries.Length) Array.Resize(ref entries, entries.Length * 2);
            entries[count] = new FloatingText
            {
                Text = text,
                AnchorId = anchorId,
                Offset = offset,
                Style = style,
                Age = 0f,
                StackIndex = stackIndex,
            };
            count++;
        }

        /// <summary>
        /// Advances every entry by <paramref name="dt"/> seconds and drops the ones whose lifetime has run out, in
        /// one pass, in place, allocating nothing.
        /// <para>Expiry is per entry rather than per store, because the style is per entry: a store holding a half
        /// second damage number and a three second level-up line ages both correctly, and the pass cannot assume the
        /// oldest is the first to go. Order is preserved for the survivors.</para>
        /// <para>A non-positive <paramref name="dt"/> ages nothing and collects nothing, so a paused frame is a
        /// no-op rather than a store that quietly keeps expiring.</para>
        /// </summary>
        public void Age(float dt)
        {
            if (dt <= 0f) return;
            int write = 0;
            for (int read = 0; read < count; read++)
            {
                FloatingText e = entries[read] with { Age = entries[read].Age + dt };
                if (e.Age >= e.Style.LifetimeSeconds) continue;
                entries[write++] = e;
            }
            // The slots above the survivors still hold a string reference each, so they are cleared rather than left
            // as garbage the store keeps alive until something happens to overwrite them.
            for (int i = write; i < count; i++) entries[i] = default;
            count = write;
        }

        /// <summary>Drops every entry.</summary>
        public void Clear()
        {
            Array.Clear(entries, 0, count);
            count = 0;
        }

        /// <summary>Drops every entry pinned to <paramref name="anchorId"/>, leaving the rest in order. What a game
        /// calls when a body dies or leaves interest, so its column does not go on drifting over empty ground.</summary>
        public void Clear(long anchorId)
        {
            int write = 0;
            for (int read = 0; read < count; read++)
            {
                if (entries[read].AnchorId == anchorId) continue;
                entries[write++] = entries[read];
            }
            for (int i = write; i < count; i++) entries[i] = default;
            count = write;
        }

        /// <summary>How many live entries are pinned to <paramref name="anchorId"/>.</summary>
        public int CountFor(long anchorId)
        {
            int n = 0;
            for (int i = 0; i < count; i++) if (entries[i].AnchorId == anchorId) n++;
            return n;
        }

        // The first entry for an anchor is its oldest, because the array is held oldest first. -1 when it holds none,
        // which Add can never see: it only asks after CountFor said there was one.
        int IndexOfOldestFor(long anchorId)
        {
            for (int i = 0; i < count; i++) if (entries[i].AnchorId == anchorId) return i;
            return -1;
        }

        void RemoveAt(int index)
        {
            if (index < 0 || index >= count) return;
            Array.Copy(entries, index + 1, entries, index, count - index - 1);
            entries[--count] = default;
        }
    }
}
