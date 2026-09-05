using System;
using System.Numerics;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// The live set of screen-fixed banners. Same lifecycle shape as <see cref="FloatingTextStore"/>: add, age once a
    /// frame, hand to <see cref="FloatingTextRenderer"/>. Usually holds nothing, occasionally one.
    /// <para>A SEPARATE TYPE rather than a mode on the anchored store, because the two hold different things. An
    /// anchored entry needs an anchor resolved to a point every frame and a stack step against its siblings, and a
    /// banner needs neither and carries two screen points instead. One type doing both would be a store with half its
    /// fields meaningless per entry and a draw path branching on which half is filled in.</para>
    /// </summary>
    public sealed class FloatingBannerStore
    {
        FloatingBanner[] entries;
        int count;

        /// <summary>A store whose backing array starts at <paramref name="capacity"/> entries and doubles from
        /// there. The default is small because a banner is an event rather than a stream.</summary>
        /// <param name="capacity">Initial capacity. Below one it is treated as one.</param>
        public FloatingBannerStore(int capacity = 4) => entries = new FloatingBanner[Math.Max(1, capacity)];

        /// <summary>How many banners are live right now.</summary>
        public int Count => count;

        /// <summary>How many banners the backing array holds before it has to grow.</summary>
        public int Capacity => entries.Length;

        /// <summary>Every live banner, oldest first, as a view over the backing array. Invalidated by the next
        /// <see cref="Add"/>, <see cref="Age"/> or <see cref="Clear"/>.</summary>
        public ReadOnlySpan<FloatingBanner> Live => entries.AsSpan(0, count);

        /// <summary>Adds one banner at age zero, travelling from <paramref name="start"/> to
        /// <paramref name="end"/>. A null or empty text is refused rather than stored.</summary>
        /// <param name="text">The already-localized line.</param>
        /// <param name="start">Design-space screen point it is centred on at birth.</param>
        /// <param name="end">Design-space screen point it is centred on at the end of its lifetime.</param>
        /// <param name="style">How it looks and dies.</param>
        public void Add(string text, Vector2 start, Vector2 end, in FloatingTextStyle style)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (count == entries.Length) Array.Resize(ref entries, entries.Length * 2);
            entries[count] = new FloatingBanner { Text = text, Style = style, Start = start, End = end, Age = 0f };
            count++;
        }

        /// <summary>Advances every banner by <paramref name="dt"/> seconds and drops the ones whose lifetime has run
        /// out, in one pass, in place, allocating nothing. A non-positive <paramref name="dt"/> is a no-op.</summary>
        public void Age(float dt)
        {
            if (dt <= 0f) return;
            int write = 0;
            for (int read = 0; read < count; read++)
            {
                FloatingBanner e = entries[read] with { Age = entries[read].Age + dt };
                if (e.Age >= e.Style.LifetimeSeconds) continue;
                entries[write++] = e;
            }
            for (int i = write; i < count; i++) entries[i] = default;
            count = write;
        }

        /// <summary>Drops every banner.</summary>
        public void Clear()
        {
            Array.Clear(entries, 0, count);
            count = 0;
        }
    }
}
