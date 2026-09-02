using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    // The AppWindow input-filter seam's whole body, headless. The frame loop calls ApplyInputFilter on the snapshot
    // BuildInput() just built (AppWindow.Frames.cs), so what is asserted here is exactly what the frame latches:
    // null passes the built snapshot through untouched, a filter's return value replaces it. Standing up a real
    // window needs a device, hence the static seam.
    public class AppWindowInputFilterTests
    {
        static InputState Built(bool focused = true) => new(
            new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            new Vector2(3, 4), Vector2.Zero, 0f, 800, 600, windowFocused: focused);

        [Fact]
        public void A_null_filter_passes_the_built_snapshot_through_unchanged()
        {
            InputState built = Built();

            Assert.Same(built, AppWindow.ApplyInputFilter(null, built));
        }

        [Fact]
        public void A_filter_replaces_the_snapshot_the_frame_sees()
        {
            InputState built = Built(focused: false);
            InputState composed = Built(focused: true);

            InputState result = AppWindow.ApplyInputFilter(_ => composed, built);

            Assert.Same(composed, result);
            Assert.True(result.WindowFocused);
        }

        [Fact]
        public void A_filter_is_handed_the_built_snapshot()
        {
            InputState built = Built();
            InputState? seen = null;

            AppWindow.ApplyInputFilter(input => { seen = input; return input; }, built);

            Assert.Same(built, seen);
        }

        [Fact]
        public void Filters_compose_through_a_lambda_without_a_new_type()
        {
            Func<InputState, InputState> first = input => input;
            Func<InputState, InputState> second = _ => Built(focused: false);
            InputState built = Built();

            InputState result = AppWindow.ApplyInputFilter(input => second(first(input)), built);

            Assert.False(result.WindowFocused);
        }
    }
}
