using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// Single-pointer gesture recognition (tap / long-press / drag) fed raw (isDown, position, dt) frames.
    /// Positions are whatever space the caller feeds (design space when driven from the design-space
    /// <c>Pointer</c>). Pure / headless; mouse-driven gestures are also exercisable live.
    /// </summary>
    public class GestureRecognizerTests
    {
        static readonly Vector2 P = new(100, 100);

        [Fact]
        public void QuickPressRelease_InPlace_IsATap()
        {
            var g = new GestureRecognizer();
            g.Update(false, P, 0f);          // idle
            g.Update(true, P, 0.016f);       // press
            Assert.False(g.Tapped);          // not until release
            g.Update(false, P, 0.016f);      // release in place, quickly

            Assert.True(g.Tapped);
            Assert.Equal(P, g.TapPosition);
        }

        [Fact]
        public void HoldingStill_PastLongPressDuration_FiresLongPressOnce()
        {
            var g = new GestureRecognizer { LongPressDuration = 0.5f };
            g.Update(true, P, 0f);
            g.Update(true, P, 0.3f);
            Assert.False(g.LongPressed);
            g.Update(true, P, 0.3f);          // total 0.6s > 0.5
            Assert.True(g.LongPressed);
            Assert.Equal(P, g.LongPressPosition);

            g.Update(true, P, 0.3f);          // still held
            Assert.False(g.LongPressed);      // fires once, not every frame
        }

        [Fact]
        public void MovingPastThreshold_StartsADrag_WithDeltaAndTotal()
        {
            var g = new GestureRecognizer { MoveThreshold = 8f };
            g.Update(true, P, 0.016f);                 // press at (100,100)
            g.Update(true, new Vector2(120, 100), 0.016f);   // move +20x -> drag starts

            Assert.True(g.DragStarted);
            Assert.True(g.IsDragging);
            Assert.Equal(new Vector2(20, 0), g.DragDelta);
            Assert.Equal(new Vector2(20, 0), g.DragTotal);
            Assert.Equal(P, g.DragStart);

            g.Update(true, new Vector2(130, 100), 0.016f);   // move +10 more
            Assert.False(g.DragStarted);
            Assert.Equal(new Vector2(10, 0), g.DragDelta);
            Assert.Equal(new Vector2(30, 0), g.DragTotal);

            g.Update(false, new Vector2(130, 100), 0.016f);  // release
            Assert.True(g.DragEnded);
            Assert.False(g.IsDragging);
        }

        [Fact]
        public void ADragIsNotAlsoATap()
        {
            var g = new GestureRecognizer { MoveThreshold = 8f };
            g.Update(true, P, 0.016f);
            g.Update(true, new Vector2(140, 100), 0.016f);   // drag
            g.Update(false, new Vector2(140, 100), 0.016f);  // release
            Assert.False(g.Tapped);
        }

        [Fact]
        public void ALongPressIsNotAlsoATapOnRelease()
        {
            var g = new GestureRecognizer { LongPressDuration = 0.5f };
            g.Update(true, P, 0f);
            g.Update(true, P, 0.6f);          // long press fires
            Assert.True(g.LongPressed);
            g.Update(false, P, 0.016f);       // release
            Assert.False(g.Tapped);
        }

        [Fact]
        public void HeldTooLongThenReleased_IsNotATap()
        {
            var g = new GestureRecognizer { TapMaxDuration = 0.4f, LongPressDuration = 5f };
            g.Update(true, P, 0f);
            g.Update(true, P, 0.5f);          // exceeds tap max but below long-press
            g.Update(false, P, 0.016f);
            Assert.False(g.Tapped);
        }
    }
}
