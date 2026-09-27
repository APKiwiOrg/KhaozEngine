using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>The history's read and write pair: it flips once per frame index, and a repeated frame index keeps the
    /// pair rather than advancing it. The scene resolves only on a frame's first render, so a later render inside the
    /// frame, such as an offscreen capture, never reaches the pair. No device needed.</summary>
    public sealed class TemporalHistoryPairTests
    {
        [Fact]
        public void The_pair_flips_once_per_frame_and_holds_within_a_frame()
        {
            var history = new TemporalHistory();
            Assert.Equal((0, 1), (history.ReadIndex, history.WriteIndex));
            history.BeginResolve(10);
            Assert.Equal((0, 1), (history.ReadIndex, history.WriteIndex));
            history.BeginResolve(10);
            Assert.Equal((0, 1), (history.ReadIndex, history.WriteIndex));
            history.BeginResolve(11);
            Assert.Equal((1, 0), (history.ReadIndex, history.WriteIndex));
            history.BeginResolve(12);
            Assert.Equal((0, 1), (history.ReadIndex, history.WriteIndex));
        }

        [Fact]
        public void Releasing_restarts_the_pair()
        {
            var history = new TemporalHistory();
            history.BeginResolve(1);
            history.BeginResolve(2);
            Assert.Equal(1, history.ReadIndex);
            history.ReleaseTargets();
            Assert.Equal((0, 1), (history.ReadIndex, history.WriteIndex));
            history.BeginResolve(3);
            Assert.Equal((0, 1), (history.ReadIndex, history.WriteIndex));
        }
    }
}
