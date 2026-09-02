using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    /// <summary>
    /// #767: the wrap memo cache is process-static, so whatever it holds strongly outlives every caller. It used
    /// to carry the <see cref="ITextMeasurer"/> INSIDE its key, which pinned a disposed <c>SpriteFont</c>'s shell
    /// and its whole glyph dictionary until 256 later wraps aged the entry out of the LRU. The cache hangs off the
    /// measurer now, so a font nobody references any more takes its entries with it.
    /// <para>
    /// No <c>DisableParallelization</c> collection: this asserts on a measurer it allocated itself, which nothing
    /// else in the assembly can reach, and the memo cache it writes is transparent (it only ever returns what
    /// recomputing would have returned). The forced collection is process-wide but harmless, since no test here
    /// asserts on timing or on the GC's own counters.
    /// </para>
    /// </summary>
    public class TextLayoutWrapCacheLifetimeTests
    {
        sealed class StubFont : ITextMeasurer
        {
            public float LineHeight => 20f;
            public Vector2 Measure(string text) => new Vector2(text.Length * 10f, 20f);
        }

        // Allocated in its own non-inlined frame so the measurer has no live root left when the collection runs.
        // Inlined into the test body it would sit in a caller local that a Debug build keeps alive to the end of
        // the method, and the result would turn on JIT lifetime policy rather than on what the cache holds.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference WrapThenDropTheMeasurer()
        {
            var font = new StubFont();
            _ = TextLayout.Wrap(font, "aaaaa bbbbb ccccc ddddd", 120f);
            _ = TextLayout.Wrap(font, "eeeee fffff ggggg", 60f, hardBreak: true);
            return new WeakReference(font);
        }

        [Fact]
        public void Wrap_AMeasurerThatGoesUnreachable_IsNotHeldByTheCache()
        {
            WeakReference weak = WrapThenDropTheMeasurer();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(weak.IsAlive, "the wrap cache is still holding a measurer nothing else references");
        }

        // The other half of the same contract: dropping the measurer must not cost a LIVE one its memo. Two
        // measurers with identical text and width are still two separate caches, so the survivor keeps serving
        // its own entry after the other one is collected.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference WrapWithASecondMeasurer(string text, float maxWidth)
        {
            var font = new StubFont();
            _ = TextLayout.Wrap(font, text, maxWidth);
            return new WeakReference(font);
        }

        [Fact]
        public void Wrap_CollectingOneMeasurer_LeavesAnotherMeasurersEntryServable()
        {
            const string Text = "hhhhh iiiii jjjjj kkkkk";
            const float MaxWidth = 120f;
            var survivor = new StubFont();
            System.Collections.Generic.List<string> before = TextLayout.Wrap(survivor, Text, MaxWidth);

            WeakReference weak = WrapWithASecondMeasurer(Text, MaxWidth);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(weak.IsAlive);
            System.Collections.Generic.List<string> after = TextLayout.Wrap(survivor, Text, MaxWidth);
            Assert.Equal(before, after);
            Assert.NotSame(before, after);
            GC.KeepAlive(survivor);
        }
    }
}
