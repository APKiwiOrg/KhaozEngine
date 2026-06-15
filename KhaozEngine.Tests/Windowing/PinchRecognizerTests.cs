using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// Two-point pinch (scale + pan) recognition. Headless-testable with synthesized point pairs; live it
    /// needs two touch points (mobile), so desktop exercises it via tests rather than a mouse.
    /// </summary>
    public class PinchRecognizerTests
    {
        [Fact]
        public void SpreadingTheFingers_ScalesUpFromTheGestureStart()
        {
            var p = new PinchRecognizer();
            p.Update(true, new Vector2(0, 0), new Vector2(100, 0));   // start, 100 apart
            Assert.True(p.IsPinching);
            Assert.Equal(1f, p.Scale, 4);
            Assert.Equal(1f, p.ScaleDelta, 4);
            Assert.Equal(new Vector2(50, 0), p.Center);

            p.Update(true, new Vector2(0, 0), new Vector2(200, 0));   // now 200 apart
            Assert.Equal(2f, p.Scale, 4);          // vs start (100 -> 200)
            Assert.Equal(2f, p.ScaleDelta, 4);     // vs previous frame (100 -> 200)
        }

        [Fact]
        public void MovingBothPoints_ReportsPanDelta()
        {
            var p = new PinchRecognizer();
            p.Update(true, new Vector2(0, 0), new Vector2(100, 0));    // center (50,0)
            p.Update(true, new Vector2(10, 20), new Vector2(110, 20)); // center (60,20)
            Assert.Equal(new Vector2(10, 20), p.PanDelta);
            Assert.Equal(1f, p.Scale, 4);          // distance unchanged
        }

        [Fact]
        public void Releasing_EndsThePinch()
        {
            var p = new PinchRecognizer();
            p.Update(true, new Vector2(0, 0), new Vector2(100, 0));
            p.Update(false, Vector2.Zero, Vector2.Zero);
            Assert.False(p.IsPinching);
            Assert.Equal(1f, p.ScaleDelta, 4);     // neutral when not pinching
        }

        [Fact]
        public void RestartingAfterRelease_RebaselinesTheScale()
        {
            var p = new PinchRecognizer();
            p.Update(true, new Vector2(0, 0), new Vector2(200, 0));
            p.Update(false, Vector2.Zero, Vector2.Zero);
            p.Update(true, new Vector2(0, 0), new Vector2(100, 0));    // fresh start at 100
            p.Update(true, new Vector2(0, 0), new Vector2(150, 0));    // -> 150
            Assert.Equal(1.5f, p.Scale, 4);        // relative to the NEW start, not the old one
        }
    }
}
