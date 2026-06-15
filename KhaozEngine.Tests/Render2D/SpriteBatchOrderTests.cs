using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    /// <summary>
    /// Guards SpriteBatch submission-order batching: quads must be grouped into runs that preserve the
    /// order they were drawn, coalescing only *consecutive* same-texture draws. The old behaviour grouped
    /// globally per texture, which let a later draw jump ahead of an earlier draw using a different texture
    /// (e.g. menu text painting on top of a modal panel drawn after it).
    /// </summary>
    public class SpriteBatchOrderTests
    {
        static readonly object TexA = new();
        static readonly object TexB = new();

        [Fact]
        public void ConsecutiveSameKeyCoalescesIntoOneRun()
        {
            var b = new QuadRunBuilder<int>();
            b.Add(TexA, 1);
            b.Add(TexA, 2);

            Assert.Single(b.Runs);
            Assert.Same(TexA, b.Runs[0].Key);
            Assert.Equal(new[] { 1, 2 }, b.Runs[0].Items);
        }

        [Fact]
        public void InterleavedTexturesProduceOrderedRunsNotGlobalGroups()
        {
            var b = new QuadRunBuilder<int>();
            b.Add(TexA, 1);   // panel-under
            b.Add(TexB, 2);   // text-over
            b.Add(TexA, 3);   // panel-on-top: must stay AFTER the text, not merge with the first A

            Assert.Equal(3, b.Runs.Count);
            Assert.Same(TexA, b.Runs[0].Key);
            Assert.Same(TexB, b.Runs[1].Key);
            Assert.Same(TexA, b.Runs[2].Key);
            Assert.Equal(new[] { 1 }, b.Runs[0].Items);
            Assert.Equal(new[] { 2 }, b.Runs[1].Items);
            Assert.Equal(new[] { 3 }, b.Runs[2].Items);
        }

        [Fact]
        public void ResetClearsRuns()
        {
            var b = new QuadRunBuilder<int>();
            b.Add(TexA, 1);
            b.Reset();

            Assert.Empty(b.Runs);
        }
    }
}
