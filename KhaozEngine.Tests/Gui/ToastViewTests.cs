using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui;

/// <summary>
/// Headless layout coverage for <see cref="ToastView.GetToastBounds"/>: corner anchoring, height clamping to
/// <see cref="ToastTheme.MinHeight"/>, wrap-driven growth, and gap-separated stacking toward the anchored
/// corner. No rendering or input involved (a later slice adds those). Layout is driven purely through
/// <see cref="ITextMeasurer"/> so it needs no GPU device or real font.
/// </summary>
public class ToastViewTests
{
    // 10px/char, 20px line height.
    sealed class FixedFont : ITextMeasurer
    {
        public float LineHeight => 20f;
        public Vector2 Measure(string text) => new(text.Length * 10f, 20f);
    }

    static readonly FixedFont Font = new();

    // Right = 970, Bottom = 560. Non-zero X/Y so an anchor test can't pass by accident against a zeroed origin.
    static readonly Rect Bounds = new(10, 20, 960, 540);

    static ToastView MakeView(ToastStack stack, ToastTheme? theme = null) =>
        new(stack, Font, theme) { Bounds = Bounds };

    [Fact]
    public void TopRight_anchors_the_newest_toast_to_the_top_right_corner()
    {
        var stack = new ToastStack();
        stack.Show(LocalizedText.Raw("hi"));
        ToastView view = MakeView(stack);

        Rect r = view.GetToastBounds(0);

        // Right(970) - Width(200) - MarginX(6) = 764; Y = Bounds.Y(20) + MarginY(6) = 26.
        Assert.Equal(764f, r.X);
        Assert.Equal(26f, r.Y);
        Assert.Equal(200f, r.Width);
    }

    [Fact]
    public void Short_message_height_clamps_to_MinHeight()
    {
        // "hi" wraps to 1 line (20px). 2*PaddingY(12) + 20 = 32, below MinHeight(36), so it clamps.
        var stack = new ToastStack();
        stack.Show(LocalizedText.Raw("hi"));
        ToastView view = MakeView(stack);

        Rect r = view.GetToastBounds(0);

        Assert.Equal(36f, r.Height);
    }

    [Fact]
    public void Long_message_wraps_to_three_lines_and_grows_past_MinHeight()
    {
        // Each word is 10 chars (100px). The text width is Width(200) - PaddingX*2(16) = 184px, so no two
        // 100px words fit on one line together: the greedy wrap emits exactly 3 lines, one word each.
        var stack = new ToastStack();
        stack.Show(LocalizedText.Raw("AAAAAAAAAA BBBBBBBBBB CCCCCCCCCC"));
        ToastView view = MakeView(stack);

        Rect r = view.GetToastBounds(0);

        // 3 lines * 20px line height = 60, plus PaddingY*2(12) = 72, which exceeds MinHeight(36).
        Assert.Equal(72f, r.Height);
    }

    [Fact]
    public void Second_toast_stacks_below_the_first_with_a_gap_in_TopRight()
    {
        // Both messages are short so each toast clamps to MinHeight(36).
        var stack = new ToastStack();
        stack.Show(LocalizedText.Raw("hi"));
        stack.Show(LocalizedText.Raw("hi"));
        ToastView view = MakeView(stack);

        Rect first = view.GetToastBounds(0);
        Rect second = view.GetToastBounds(1);

        Assert.Equal(26f, first.Y);
        Assert.Equal(36f, first.Height);
        // second.Y = first.Y(26) + first.Height(36) + Gap(4) = 66.
        Assert.Equal(66f, second.Y);
    }

    [Fact]
    public void BottomRight_stacks_upward_from_the_bottom_edge()
    {
        var theme = new ToastTheme { Corner = OverlayCorner.BottomRight };
        var stack = new ToastStack();
        stack.Show(LocalizedText.Raw("hi"));
        stack.Show(LocalizedText.Raw("hi"));
        ToastView view = MakeView(stack, theme);

        Rect first = view.GetToastBounds(0);
        Rect second = view.GetToastBounds(1);

        // Bottom(560) - MarginY(6) = 554 is toast 0's bottom edge; toast 1 sits above it, separated by Gap(4).
        Assert.Equal(554f, first.Bottom);
        Assert.Equal(first.Y - theme.Gap, second.Bottom);
    }

    [Fact]
    public void TopLeft_anchors_X_to_the_left_margin()
    {
        var theme = new ToastTheme { Corner = OverlayCorner.TopLeft };
        var stack = new ToastStack();
        stack.Show(LocalizedText.Raw("hi"));
        ToastView view = MakeView(stack, theme);

        Rect r = view.GetToastBounds(0);

        // Bounds.X(10) + MarginX(6) = 16.
        Assert.Equal(16f, r.X);
    }
}
