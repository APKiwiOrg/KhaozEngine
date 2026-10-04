using System;
using System.Numerics;
using KhaozEngine.Game;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    /// <summary>
    /// <c>Advance</c> runs every frame per animated character, so a settled gait and a reversal crossfade must allocate
    /// nothing. Joins <c>AllocSensitive</c> because it reads <see cref="System.GC.GetAllocatedBytesForCurrentThread"/>.
    /// </summary>
    [Collection("AllocSensitive")]
    public class DirectionalLocomotionBlendAllocationTests
    {
        [Fact]
        public void AdvanceAllocatesNothing()
        {
            var blend = new DirectionalLocomotionBlend(DirectionalLocomotionBlendTests.Set());
            var samples = new GaitSample[6];
            Vector2 diagonal = new Vector2(1f, 1f) / MathF.Sqrt(2f) * 2.7f;
            var forward = new Vector2(0f, 1.4f);
            var back = new Vector2(0f, -1f);
            DirectionalLocomotionBlendTests.Settle(blend, diagonal, samples);

            int written = 0;
            AllocAssert.NoPerCallAllocation("DirectionalLocomotionBlend.Advance", () =>
            {
                for (int i = 0; i < 60; i++) written = blend.Advance(diagonal, 1f / 60f, samples);
                for (int i = 0; i < 10; i++) blend.Advance(forward, 1f / 60f, samples);
                for (int i = 0; i < 10; i++) written = blend.Advance(back, 1f / 60f, samples);
            });

            Assert.Equal(1, written);
        }
    }
}
