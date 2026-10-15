using System;
using System.Threading.Tasks;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui;

[Collection("gui-theme-global")]
public sealed class GuiThemeScopeTests
{
    [Fact]
    public void WidgetsCaptureTheInnermostThemeAndKeepItAfterUnwind()
    {
        var previous = GuiTheme.Default;
        try
        {
            GuiTheme.Default = GuiTheme.Crisp;
            Toggle outerWidget;
            using (GuiTheme.Use(GuiTheme.Legacy))
            {
                outerWidget = new Toggle(new Rect(0, 0, 40, 20));
                Assert.Equal(GuiTheme.Legacy.Surface, outerWidget.OffColor);
                Assert.Throws<InvalidOperationException>((Action)(() =>
                {
                    using var inner = GuiTheme.Use(GuiTheme.Crisp);
                    Assert.Equal(GuiTheme.Crisp.Surface, new Toggle(new Rect()).OffColor);
                    throw new InvalidOperationException();
                }));
                Assert.Equal(GuiTheme.Legacy.Surface, GuiTheme.Default.Surface);
            }
            Assert.Equal(GuiTheme.Crisp.Surface, new Toggle(new Rect()).OffColor);
            Assert.Equal(GuiTheme.Legacy.Surface, outerWidget.OffColor);
        }
        finally { GuiTheme.Default = previous; }
    }

    [Fact]
    public void SnapshotControlsRetainColorsWhileLiveReadersFollowEachScope()
    {
        var previous = GuiTheme.Default;
        try
        {
            GuiTheme.Default = GuiTheme.Crisp;
            Toggle toggle;
            PatchNotesView view;
            using (GuiTheme.Use(GuiTheme.Legacy))
            {
                toggle = new Toggle(new Rect(0, 0, 40, 20));
                view = new PatchNotesView(PatchNotesDocument.Empty);
                Assert.Equal(GuiTheme.Legacy.Surface, toggle.OffColor);
                Assert.Equal((Color)GuiTheme.Legacy.Surface, view.Theme.PanelFill);
            }

            Assert.Equal(GuiTheme.Legacy.Surface, toggle.OffColor);
            Assert.Equal((Color)GuiTheme.Crisp.Surface, view.Theme.PanelFill);

            using (GuiTheme.Use(GuiTheme.Legacy))
                Assert.Equal((Color)GuiTheme.Legacy.Surface, view.Theme.PanelFill);

            Assert.Equal((Color)GuiTheme.Crisp.Surface, view.Theme.PanelFill);
            Assert.Equal(GuiTheme.Legacy.Surface, toggle.OffColor);
        }
        finally { GuiTheme.Default = previous; }
    }

    [Fact]
    public void SetterInsideScopeChangesProcessFallbackAndScopesCaptureValueCopies()
    {
        var previous = GuiTheme.Default;
        try
        {
            GuiTheme.Default = GuiTheme.Crisp;
            var theme = GuiTheme.Legacy;
            using (GuiTheme.Use(theme))
            {
                theme.Surface = default;
                GuiTheme.Default = GuiTheme.Crisp with { CornerRadius = 17 };
                Assert.Equal(GuiTheme.Legacy.Surface, GuiTheme.Default.Surface);
                Assert.Equal(0, GuiTheme.Default.CornerRadius);
            }
            Assert.Equal(17, GuiTheme.Default.CornerRadius);
        }
        finally { GuiTheme.Default = previous; }
    }

    [Fact]
    public void OutOfOrderDisposalThrowsAndRepeatDisposalDoesNotPopAnotherScope()
    {
        using var outer = GuiTheme.Use(GuiTheme.Legacy);
        var inner = GuiTheme.Use(GuiTheme.Crisp);
        try
        {
            Assert.Throws<InvalidOperationException>(outer.Dispose);
            Assert.Equal(GuiTheme.Crisp.Surface, GuiTheme.Default.Surface);
        }
        finally { inner.Dispose(); }
        inner.Dispose();
        Assert.Equal(GuiTheme.Legacy.Surface, GuiTheme.Default.Surface);
    }

    [Fact]
    public async Task ConcurrentChildrenInheritAndDisposeIndependentlyAcrossAwaits()
    {
        var previous = GuiTheme.Default;
        try
        {
            GuiTheme.Default = GuiTheme.Crisp;
            using var parent = GuiTheme.Use(GuiTheme.Legacy);
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task child = Task.Run(async () =>
            {
                Assert.Equal(GuiTheme.Legacy.Surface, GuiTheme.Default.Surface);
                parent.Dispose();
                parent.Dispose();
                Assert.Equal(GuiTheme.Crisp.Surface, GuiTheme.Default.Surface);
                using var own = GuiTheme.Use(GuiTheme.Crisp with { CornerRadius = 9 });
                changed.SetResult();
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(9, GuiTheme.Default.CornerRadius);
            });
            Task sibling = Task.Run(async () =>
            {
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(GuiTheme.Legacy.Surface, GuiTheme.Default.Surface);
                using (GuiTheme.Use(GuiTheme.Crisp))
                {
                    await Task.Yield();
                    Assert.Equal(GuiTheme.Crisp.Surface, GuiTheme.Default.Surface);
                }
                Assert.Equal(GuiTheme.Legacy.Surface, GuiTheme.Default.Surface);
                observed.SetResult();
            });
            await Task.WhenAll(child, sibling);
            Assert.Equal(GuiTheme.Legacy.Surface, GuiTheme.Default.Surface);
            parent.Dispose();
            Assert.Equal(GuiTheme.Crisp.Surface, GuiTheme.Default.Surface);
        }
        finally { GuiTheme.Default = previous; }
    }

    [Fact]
    public void NullNullableThemeIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => GuiTheme.Use((GuiTheme?)null));
        using (GuiTheme.Use((GuiTheme?)GuiTheme.Legacy))
            Assert.Equal(GuiTheme.Legacy.Surface, GuiTheme.Default.Surface);
    }
}
