using System.Numerics;
using KhaozEngine.Primitives;
using Xunit;
using static KhaozEngine.Tests.InteriorCut.InteriorHeightFieldTests;

namespace KhaozEngine.Tests.InteriorCut;

public class InteriorRoofConservativeTests
{
    [Fact]
    public void ShiftedThinSlabNeverCutsAboveTheLowSideOfAnyCell()
    {
        const float low = -0.123f, high = 1.123f;
        var slab = new ClearanceMesh(
            [new(low, low + 1f, low), new(high, high + 1f, low),
             new(high, high + 1f, high), new(low, low + 1f, high),
             new(low, low + 0.99f, low), new(high, high + 0.99f, low),
             new(high, high + 0.99f, high), new(low, low + 0.99f, high)],
            [0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 4, 5, 0, 5, 1,
             1, 5, 6, 1, 6, 2, 2, 6, 7, 2, 7, 3, 3, 7, 4, 3, 4, 0]);
        var volume = Rect(0f, 0f, 1f, 1f, top: 1f) with
        {
            Roofs = [new(ClearanceSamples.Build(slab, 0.125f), Matrix4x4.Identity)],
        };
        var field = InteriorHeightFieldBuilder.Build([volume], new InteriorWindow(Vector2.Zero, 0.25f, 4), new(0f, 0.01f));

        Assert.False(field.Contains(new Vector3(0.255f, 1.27f, 0.375f)));
        for (int z = 0; z < 4; z++)
            for (int x = 0; x < 4; x++)
            {
                Assert.True(field.TryGetSpan(x, z, out _, out float top));
                float undersideAtLowEdge = x * 0.25f + 0.99f;
                Assert.True(top <= undersideAtLowEdge, $"Cell ({x}, {z}) top {top} exceeds {undersideAtLowEdge}");
                Assert.InRange(undersideAtLowEdge - top, 0f, field.StepY);
                Assert.False(field.Contains(new Vector3(x * 0.25f + 0.005f, x * 0.25f + 1.01f, z * 0.25f + 0.125f)));
            }
    }
}
