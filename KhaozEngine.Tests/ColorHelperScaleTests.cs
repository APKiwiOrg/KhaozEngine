using KhaozEngine.Graphics;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class ColorHelperScaleTests
{
    [Fact]
    public void Scale_darkens_rgb_and_leaves_alpha_untouched()
    {
        Color result = ColorHelper.Scale(new Color(200, 100, 50, 128), 0.5f);
        Assert.Equal(new Color(100, 50, 25, 128), result);
    }

    [Fact]
    public void Scale_above_one_clamps_to_255()
    {
        Color result = ColorHelper.Scale(new Color(200, 200, 200, 255), 2f);
        Assert.Equal(new Color(255, 255, 255, 255), result);
    }

    [Fact]
    public void Scale_with_negative_factor_clamps_to_black()
    {
        Color result = ColorHelper.Scale(new Color(200, 100, 50, 200), -1f);
        Assert.Equal(new Color(0, 0, 0, 200), result);
    }
}
