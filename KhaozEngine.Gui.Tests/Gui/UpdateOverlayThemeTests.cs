using KhaozEngine.Gui;
using KhaozEngine.Tests.Updates; // for FakeUpdateStatus
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class UpdateOverlayThemeTests
{
    [Fact]
    public void Default_titles_match_state()
    {
        var t = UpdateOverlayTheme.Default;
        Assert.Equal("Update Available - v1.2.3", t.TitleFor(UpdateState.UpdateAvailable, "1.2.3"));
        Assert.Equal("Update v1.2.3 Ready", t.TitleFor(UpdateState.ReadyToApply, "1.2.3"));
        Assert.Equal("Update Failed", t.TitleFor(UpdateState.Failed, (string?)null));
    }

    [Fact]
    public void Body_uses_trigger_key_label()
    {
        var t = UpdateOverlayTheme.Default;
        t.TriggerKeyLabel = "X";
        Assert.Equal("Press [X] to download", t.BodyFor(UpdateState.UpdateAvailable, new FakeUpdateStatus()));
    }

    [Fact]
    public void Downloading_body_reports_progress()
    {
        var t = UpdateOverlayTheme.Default;
        var s = new FakeUpdateStatus
        {
            State = UpdateState.Downloading,
            FilesDownloaded = 2,
            TotalFilesToDownload = 5,
            BytesDownloaded = 3 * 1024 * 1024,
            TotalDownloadBytes = 10 * 1024 * 1024,
        };
        Assert.Equal("Downloading 2/5 files (3.0/10.0 MB)", t.BodyFor(UpdateState.Downloading, s));
    }

    [Fact]
    public void AccentFor_differs_between_ready_and_failed()
    {
        var t = UpdateOverlayTheme.Default;
        Assert.NotEqual(t.AccentFor(UpdateState.ReadyToApply), t.AccentFor(UpdateState.Failed));
    }

    [Fact]
    public void Required_status_titles_use_required_variant()
    {
        var t = UpdateOverlayTheme.Default;
        var s = new FakeUpdateStatus { IsRequired = true, RemoteVersion = "1.2.3" };
        Assert.Equal("Required Update - v1.2.3", t.TitleFor(UpdateState.UpdateAvailable, s));
        Assert.Equal("Downloading Required Update...", t.TitleFor(UpdateState.Downloading, s));
        Assert.Equal("Required Update v1.2.3 Ready", t.TitleFor(UpdateState.ReadyToApply, s));
        Assert.Equal("Applying Required Update...", t.TitleFor(UpdateState.Applying, s));
        Assert.Equal("Required Update Failed", t.TitleFor(UpdateState.Failed, s));
    }

    [Fact]
    public void Required_status_bodies_drop_the_keypress_prompt()
    {
        var t = UpdateOverlayTheme.Default;
        t.TriggerKeyLabel = "X";
        var s = new FakeUpdateStatus { IsRequired = true };
        Assert.Equal("A required update is downloading", t.BodyFor(UpdateState.UpdateAvailable, s));
        Assert.Equal("Restarting to apply", t.BodyFor(UpdateState.ReadyToApply, s));
        // Failed keeps the keypress retry even when required.
        Assert.Equal("Press [X] to retry", t.BodyFor(UpdateState.Failed, s));
    }

    [Fact]
    public void Failed_body_drops_the_retry_prompt_once_the_apply_budget_is_spent()
    {
        var t = UpdateOverlayTheme.Default;
        Assert.Equal("Press [U] to retry",
            t.BodyFor(UpdateState.Failed, new FakeUpdateStatus { State = UpdateState.Failed }));
        Assert.Equal("This update could not be installed",
            t.BodyFor(UpdateState.Failed,
                new FakeUpdateStatus { State = UpdateState.Failed, ApplyAttemptsExhausted = true }));
    }

    [Fact]
    public void Hint_offers_the_dismiss_key_on_a_dismissible_panel_only()
    {
        var t = UpdateOverlayTheme.Default;
        var optional = new FakeUpdateStatus();
        Assert.Equal("Press [Esc] to dismiss", t.HintFor(UpdateState.UpdateAvailable, optional));
        Assert.Equal("Press [Esc] to dismiss", t.HintFor(UpdateState.ReadyToApply, optional));
        Assert.Equal("Press [Esc] to dismiss", t.HintFor(UpdateState.Failed, optional));
        Assert.Equal(string.Empty, t.HintFor(UpdateState.Downloading, optional));
        Assert.Equal(string.Empty, t.HintFor(UpdateState.Applying, optional));
        // A required update is never dismissible, so it never advertises a key that does nothing.
        Assert.Equal(string.Empty,
            t.HintFor(UpdateState.UpdateAvailable, new FakeUpdateStatus { IsRequired = true }));
    }

    [Fact]
    public void Hint_uses_the_rebound_dismiss_key_label()
    {
        var t = UpdateOverlayTheme.Default;
        t.DismissKeyLabel = "Q";
        Assert.Equal("Press [Q] to dismiss", t.HintFor(UpdateState.Failed, new FakeUpdateStatus()));
    }

    [Fact]
    public void Optional_status_overload_delegates_to_the_version_titles()
    {
        var t = UpdateOverlayTheme.Default;
        var s = new FakeUpdateStatus { IsRequired = false, RemoteVersion = "1.2.3" };
        Assert.Equal("Update Available - v1.2.3", t.TitleFor(UpdateState.UpdateAvailable, s));
        Assert.Equal("Press [U] to download", t.BodyFor(UpdateState.UpdateAvailable, s));
    }
}
