using System;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;
using KhaozEngine.BugReports.Contracts;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client.Screen;

public sealed class BugReportScreenModelTests
{
    static BugReportScreenModel Model(bool screenshot = true, bool clip = true) =>
        new(BugReportDraft.DefaultCategories, signedIn: true, screenshotAvailable: screenshot, clipAvailable: clip);

    [Fact]
    public void Defaults_to_Other_and_cannot_send_blank()
    {
        BugReportScreenModel model = Model();

        Assert.Equal(5, model.CategoryIndex);
        Assert.Equal("Other", model.Category);
        Assert.Equal("", model.Description);
        Assert.False(model.CanSend);
        Assert.True(model.IncludeScreenshot);
        Assert.True(model.IncludeClip);

        model.Description = " \n\t ";
        Assert.False(model.CanSend);
        model.Description = "  x ";
        Assert.True(model.CanSend);

        var custom = new BugReportScreenModel(["Crafting", "Combat"], signedIn: false, screenshotAvailable: true, clipAvailable: true);
        Assert.Equal(0, custom.CategoryIndex);
        Assert.Equal("Crafting", custom.Category);
        Assert.False(custom.SignedIn);
        custom.CategoryIndex = 1;
        Assert.Equal("Combat", custom.Category);
        Assert.Throws<ArgumentOutOfRangeException>(() => custom.CategoryIndex = 2);

        var draft = new BugReportDraft(ReportIds.NewId(), "game", BugReportKind.Bug, DateTimeOffset.UnixEpoch,
            [], null, null);
        model.Description = "Fell through";
        model.CategoryIndex = 1;
        model.IncludeClip = false;
        model.ApplyTo(draft);
        Assert.Equal("Fell through", draft.Description);
        Assert.Equal("Visual", draft.Category);
        Assert.True(draft.IncludeScreenshot);
        Assert.False(draft.IncludeClip);
    }

    [Fact]
    public void Unavailable_media_cannot_be_included()
    {
        BugReportScreenModel model = Model(screenshot: false, clip: false);

        Assert.False(model.IncludeScreenshot);
        Assert.False(model.IncludeClip);
        model.IncludeScreenshot = true;
        model.IncludeClip = true;
        Assert.False(model.IncludeScreenshot);
        Assert.False(model.IncludeClip);

        BugReportScreenModel faulted = Model();
        Assert.True(faulted.IncludeScreenshot);
        faulted.ScreenshotAvailable = false;
        Assert.False(faulted.IncludeScreenshot);
    }

    [Fact]
    public void Description_is_clamped_to_4000()
    {
        BugReportScreenModel model = Model();

        model.Description = new string('a', BugReportLimits.MaxDescriptionChars + 25);
        Assert.Equal(4000, model.Description.Length);

        // A surrogate pair straddling the limit is dropped whole rather than split.
        model.Description = new string('a', 3999) + "\U0001F600";
        Assert.Equal(3999, model.Description.Length);

        model.Description = null!;
        Assert.Equal("", model.Description);
    }
}
