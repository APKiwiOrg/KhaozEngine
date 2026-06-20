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
        Assert.Equal("Update Failed", t.TitleFor(UpdateState.Failed, null));
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
}
