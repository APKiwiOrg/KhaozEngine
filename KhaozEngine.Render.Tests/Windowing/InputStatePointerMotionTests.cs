using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class InputStatePointerMotionTests
{
    static readonly IReadOnlySet<Key> NoKeys = new HashSet<Key>();
    static readonly IReadOnlySet<MouseButton> NoButtons = new HashSet<MouseButton>();

    [Fact]
    public void Old_constructor_and_empty_snapshot_keep_the_one_scale_motion_contract()
    {
        var input = new InputState(NoKeys, NoKeys, NoKeys, NoButtons, NoButtons,
            new Vector2(100, 150), new Vector2(6, 9), 0, 1920, 1080, pointerCaptured: true);
        Assert.Equal(input.MouseDelta, input.MouseDeltaPoints);
        Assert.Equal(Vector2.One, input.FramebufferScale);
        Assert.Equal(Vector2.Zero, InputState.Empty.MouseDeltaPoints);
        Assert.Equal(Vector2.One, InputState.Empty.FramebufferScale);
    }

    [Fact]
    public void Old_binary_constructor_and_all_optional_defaults_remain_available()
    {
        var constructor = typeof(InputState).GetConstructor(new[]
        {
            typeof(IReadOnlySet<Key>), typeof(IReadOnlySet<Key>), typeof(IReadOnlySet<Key>),
            typeof(IReadOnlySet<MouseButton>), typeof(IReadOnlySet<MouseButton>),
            typeof(Vector2), typeof(Vector2), typeof(float), typeof(int), typeof(int),
            typeof(IReadOnlyList<GamepadState>), typeof(IReadOnlyList<TouchPoint>), typeof(bool),
            typeof(IReadOnlySet<Key>), typeof(IReadOnlySet<MouseButton>), typeof(string), typeof(bool), typeof(bool),
        });
        Assert.NotNull(constructor);
        var parameters = constructor.GetParameters();
        Assert.Equal(18, parameters.Length);
        object?[] defaults = { null, null, true, null, null, "", false, false };
        for (int index = 0; index < defaults.Length; index++)
        {
            Assert.True(parameters[index + 10].IsOptional);
            Assert.Equal(defaults[index], parameters[index + 10].DefaultValue);
        }
        var literal = new InputState(NoKeys, NoKeys, NoKeys, NoButtons, NoButtons,
            default, default, 0, 1920, 1080);
        Assert.Equal(Vector2.One, literal.FramebufferScale);
        var added = Array.Find(typeof(InputState).GetConstructors(), c => c.GetParameters().Length == 20);
        Assert.NotNull(added);
        Assert.Equal(typeof(Vector2), added.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(Vector2), added.GetParameters()[1].ParameterType);
        Assert.False(added.GetParameters()[0].IsOptional);
        Assert.False(added.GetParameters()[1].IsOptional);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(float.NaN, float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity, 0)]
    public void Invalid_scale_axes_default_to_one_without_changing_explicit_motion(float x, float y)
    {
        var input = Frame(new Vector2(3, 2), new Vector2(x, y));
        Assert.Equal(Vector2.One, input.FramebufferScale);
        Assert.Equal(new Vector2(3, 2), input.MouseDeltaPoints);
        Assert.Equal(new Vector2(6, 6), input.MouseDelta);
    }

    [Fact]
    public void Scale_normalization_is_per_axis_and_does_not_clamp_valid_fractional_scale()
    {
        Assert.Equal(new Vector2(2, 1), Frame(Vector2.Zero, new Vector2(2, float.NaN)).FramebufferScale);
        Assert.Equal(new Vector2(1, 0.5f), Frame(Vector2.Zero, new Vector2(-2, 0.5f)).FramebufferScale);
    }

    [Fact]
    public void WithoutScroll_preserves_both_motion_facts_and_collection_identity()
    {
        var input = Frame(new Vector2(3, 2), new Vector2(2, 3), scroll: 2);
        var cleared = input.WithoutScroll();
        Assert.Equal(Vector2.Zero, cleared.MousePosition);
        Assert.Equal(input.MouseDelta, cleared.MouseDelta);
        Assert.Equal(input.MouseDeltaPoints, cleared.MouseDeltaPoints);
        Assert.Equal(input.FramebufferScale, cleared.FramebufferScale);
        Assert.True(cleared.PointerCaptured);
        Assert.Same(input.KeysDown, cleared.KeysDown);
        Assert.Same(input.MouseDown, cleared.MouseDown);
        Assert.Equal(0, cleared.ScrollDelta);
        Assert.Same(cleared, cleared.WithoutScroll());
    }

    static InputState Frame(Vector2 points, Vector2 scale, float scroll = 0)
        => new(points, scale, NoKeys, NoKeys, NoKeys, NoButtons, NoButtons,
            Vector2.Zero, new Vector2(6, 6), scroll, 1920, 1080, pointerCaptured: true);
}
