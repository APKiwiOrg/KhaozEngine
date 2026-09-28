using System.Collections.Generic;
using KhaozEngine.Game;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// Where <c>GameApp</c> ticks its <see cref="GameClock"/>, which is what makes <see cref="GameClock.FrameCount"/>
    /// one id for a whole frame (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1189">#1189</see>).
    /// <para>
    /// <c>GameApp</c> needs a real window, so its loop is not constructed here. What is driven is production code on
    /// both sides of it: <see cref="FramePhases"/> is the whole of what <c>AppWindow.Run</c> does per frame, and
    /// <see cref="GameApp.StartFrame"/> is the head of <c>GameApp</c>'s prepare phase, which ticks the clock and then
    /// runs the rest of the phase, <c>OnUpdate</c> included. The record phase does not touch the clock. What is left
    /// uncovered is the one-line call from the prepare phase, which is compile-checked.
    /// </para>
    /// </summary>
    public sealed class GameAppFrameClockTests
    {
        static Frame NewFrame(IGpuCommandList commands, bool renderSuppressed) => new()
        {
            Dt = 1f / 60f,
            Width = 320,
            Height = 200,
            LogicalWidth = 320,
            LogicalHeight = 200,
            Commands = commands,
            RenderSuppressed = renderSuppressed,
        };

        [Fact]
        public void The_clock_ticks_once_per_frame_before_the_update_so_update_and_draw_share_one_id()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList frameList = device.Factory.CreateCommandList();
            var clock = new GameClock();
            var autoPause = new FocusAutoPause(enabled: false);
            var seenByUpdate = new List<long>();
            var seenByDraw = new List<long>();

            // A minimized frame skips render and present but still runs update, so it still advances the id.
            foreach (bool suppressed in new[] { false, true, false, false })
            {
                FramePhases.Run(NewFrame(frameList, suppressed), render: !suppressed, device, frameList, Color.Black,
                    onPrepare: frame => GameApp.StartFrame(autoPause, clock, frame,
                        _ => seenByUpdate.Add(clock.FrameCount)),
                    onFrame: _ => seenByDraw.Add(clock.FrameCount));
            }

            Assert.Equal(new long[] { 1, 2, 3, 4 }, seenByUpdate);
            Assert.Equal(seenByUpdate, seenByDraw);
        }
    }
}
