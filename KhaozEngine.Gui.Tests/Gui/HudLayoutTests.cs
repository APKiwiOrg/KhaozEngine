using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class HudLayoutTests
{
    static readonly Rect Default = new(20f, 30f, 200f, 100f);

    [Fact]
    public void SavedCenterFollowsTheViewportWithoutChangingTheElementSize()
    {
        var saved = new HudLayoutPosition(0.75f, 0.25f);

        Assert.Equal(new Rect(860f, 130f, 200f, 100f),
            HudLayout.Resolve(Default, new Vector2(1280f, 720f), saved));
        Assert.Equal(new Rect(380f, 40f, 200f, 100f),
            HudLayout.Resolve(Default, new Vector2(640f, 360f), saved));
        Assert.Equal(new HudLayoutPosition(0.75f, 0.25f), saved);
    }

    [Fact]
    public void ClampingKeepsTheFullElementVisibleWithoutRewritingItsSavedCenter()
    {
        var saved = new HudLayoutPosition(1f, 0f);

        Assert.Equal(new Rect(440f, 0f, 200f, 100f),
            HudLayout.Resolve(Default, new Vector2(640f, 360f), saved));
        Assert.Equal(new HudLayoutPosition(1f, 0f), saved);
        Assert.Equal(new Rect(1080f, 0f, 200f, 100f),
            HudLayout.Resolve(Default, new Vector2(1280f, 720f), saved));
    }

    [Theory]
    [InlineData(float.NaN, 0.5f)]
    [InlineData(0.5f, float.PositiveInfinity)]
    [InlineData(-1f, 0.5f)]
    [InlineData(0.5f, 2f)]
    public void InvalidSavedCoordinatesFallBackToTheCallersDefault(float x, float y)
    {
        Assert.Equal(Default,
            HudLayout.Resolve(Default, new Vector2(1280f, 720f), new HudLayoutPosition(x, y)));
    }

    [Fact]
    public void AnOversizedElementKeepsItsTopLeftReachable()
    {
        Assert.Equal(new Rect(0f, 0f, 200f, 100f),
            HudLayout.Resolve(Default, new Vector2(100f, 80f), new HudLayoutPosition(0.5f, 0.5f)));
    }

    [Fact]
    public void ACanvasBeforeItsFirstSizedFrameKeepsTheDefaultReachable()
    {
        Assert.Equal(new Rect(0f, 0f, 200f, 100f), HudLayout.Resolve(Default, Vector2.Zero, null));
    }

    [Fact]
    public void BoundsBecomeNormalizedSerializableCenters()
    {
        var saved = new Dictionary<string, HudLayoutPosition>
        {
            ["frame"] = HudLayout.FromBounds(new Rect(860f, 130f, 200f, 100f), new Vector2(1280f, 720f)),
        };
        string json = JsonSerializer.Serialize(saved);
        var restored = JsonSerializer.Deserialize<Dictionary<string, HudLayoutPosition>>(json)!;

        Assert.Equal(new HudLayoutPosition(0.75f, 0.25f), restored["frame"]);
        Assert.Equal(new Rect(860f, 130f, 200f, 100f),
            HudLayout.Resolve(Default, new Vector2(1280f, 720f), restored["frame"]));
    }
}
