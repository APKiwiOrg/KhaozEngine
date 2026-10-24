using System.Reflection;
using KhaozEngine.Game;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class RendererSwapchainOptionsTests
{
    [Fact]
    public void Every_options_construction_keeps_host_initialization_unless_explicitly_enabled()
    {
        FieldInfo? field = typeof(GameAppOptions).GetField("RendererInitializesSwapchain");
        Assert.NotNull(field);
        Assert.Equal(typeof(bool), field.FieldType);
        foreach (GameAppOptions options in new[] { default(GameAppOptions), new GameAppOptions { Title = "raw" },
            GameAppOptions.For("defaults", 64, 32) })
            Assert.Equal(false, field.GetValue(options));
    }

    [Fact]
    public void The_window_exposes_a_writable_positive_opt_in()
    {
        PropertyInfo? property = typeof(AppWindow).GetProperty("RendererInitializesSwapchain");
        Assert.NotNull(property);
        Assert.Equal(typeof(bool), property.PropertyType);
        Assert.True(property.CanRead);
        Assert.True(property.CanWrite);
    }
}
