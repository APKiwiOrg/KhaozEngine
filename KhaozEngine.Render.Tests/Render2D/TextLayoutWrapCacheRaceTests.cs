using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    /// <summary>
    /// #87: <see cref="TextLayout.Wrap"/> releases its cache lock across the wrap computation, so two callers can
    /// both miss on the same key and both reach the insert. The insert used to be unconditional, which left the
    /// first caller's node in the LRU list with no index row pointing at it. Structural, not timing based: the
    /// interleaving is forced by a measurer that re-enters Wrap from inside the outer call's unlocked gap, and
    /// the assertion is the cache's own invariant (one list node per index entry) rather than a count that other
    /// tests running in parallel could move.
    /// <para>
    /// No <c>DisableParallelization</c> collection: the memo cache this writes is transparent (it only ever
    /// returns what recomputing would have returned), unlike the ambient statics that rule is written for, and
    /// the invariant asserted here holds at every lock boundary no matter who else is wrapping at the time.
    /// </para>
    /// </summary>
    public class TextLayoutWrapCacheRaceTests
    {
        // Re-enters Wrap exactly ONCE, from inside the first measurement the outer call makes. The inner call
        // therefore completes and inserts while the outer call is still computing and holding no lock, which is
        // precisely the state two racing threads produce, reached here with no threads and no timing.
        sealed class ReentrantFont : ITextMeasurer
        {
            public string Text = "";
            public float MaxWidth;
            bool _reentered;

            public float LineHeight => 20f;

            public Vector2 Measure(string text)
            {
                if (!_reentered)
                {
                    _reentered = true;
                    _ = TextLayout.Wrap(this, Text, MaxWidth);   // same key, runs to completion and inserts
                }
                return new Vector2(text.Length * 10f, 20f);
            }
        }

        [Fact]
        public void Wrap_LosingTheRaceToInsert_DoesNotOrphanAnLruNode()
        {
            var font = new ReentrantFont { Text = "aaaaa bbbbb ccccc ddddd", MaxWidth = 120f };

            var lines = TextLayout.Wrap(font, font.Text, font.MaxWidth);

            // The wrap itself is unaffected by who won: same font, same text, same width.
            Assert.Equal(new[] { "aaaaa bbbbb", "ccccc ddddd" }, lines);

            (int indexCount, int lruCount) = TextLayout.WrapCacheCounts();
            Assert.Equal(indexCount, lruCount);
        }

        [Fact]
        public void Wrap_AfterALostRace_StillServesTheKeyFromTheCache()
        {
            var font = new ReentrantFont { Text = "xxxx yyyy zzzz wwww", MaxWidth = 90f };

            var duringRace = TextLayout.Wrap(font, font.Text, font.MaxWidth);
            var afterRace = TextLayout.Wrap(font, font.Text, font.MaxWidth);   // the winner's entry, reused

            Assert.Equal(duringRace, afterRace);
            Assert.NotSame(duringRace, afterRace);

            (int indexCount, int lruCount) = TextLayout.WrapCacheCounts();
            Assert.Equal(indexCount, lruCount);
        }
    }
}
