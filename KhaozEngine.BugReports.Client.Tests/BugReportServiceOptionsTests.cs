using System;
using System.Linq;
using KhaozEngine.BugReports.Client;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client;

public sealed class BugReportServiceOptionsTests
{
    static BugReportServiceOptions Valid() => new() { GameId = "grimhollow", GameDisplayName = "Grimhollow" };

    [Fact]
    public void Defaults_match_the_contract()
    {
        BugReportServiceOptions options = Valid();

        Assert.Equal(Key.F10, options.DefaultKey);
        Assert.True(options.RecordingEnabled);
        Assert.Null(options.Categories);
        Assert.Equal(new FrameHistoryOptions(), options.Recording);
        Assert.Null(options.TriggerBindingsJson);
        Assert.Null(options.SaveTriggerBindings);
        Assert.Null(options.SessionLogPath);
        BugReportServiceOptions.Validate(options);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Grim Hollow")]
    [InlineData("-grim")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]
    public void Invalid_game_id_is_rejected(string id)
    {
        Assert.ThrowsAny<ArgumentException>(() => BugReportServiceOptions.Validate(Valid() with { GameId = id }));
    }

    [Fact]
    public void Blank_display_name_is_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => BugReportServiceOptions.Validate(Valid() with { GameDisplayName = " " }));
    }

    [Fact]
    public void Too_many_or_blank_categories_are_rejected()
    {
        string[] sixteen = Enumerable.Range(0, 16).Select(i => "Cat" + i).ToArray();
        BugReportServiceOptions.Validate(Valid() with { Categories = sixteen });
        BugReportServiceOptions.Validate(Valid() with { Categories = [new string('c', 64)] });

        Assert.ThrowsAny<ArgumentException>(() =>
            BugReportServiceOptions.Validate(Valid() with { Categories = [.. sixteen, "Extra"] }));
        Assert.ThrowsAny<ArgumentException>(() => BugReportServiceOptions.Validate(Valid() with { Categories = [] }));
        Assert.ThrowsAny<ArgumentException>(() => BugReportServiceOptions.Validate(Valid() with { Categories = ["Gameplay", " "] }));
        Assert.ThrowsAny<ArgumentException>(() => BugReportServiceOptions.Validate(Valid() with { Categories = ["Gameplay", ""] }));
        Assert.ThrowsAny<ArgumentException>(() => BugReportServiceOptions.Validate(Valid() with { Categories = ["Gameplay", null!] }));
        Assert.ThrowsAny<ArgumentException>(() =>
            BugReportServiceOptions.Validate(Valid() with { Categories = [new string('c', 65)] }));
    }

    [Fact]
    public void Ill_formed_category_ids_are_rejected()
    {
        BugReportServiceOptions.Validate(Valid() with { Categories = ["Bugs \uD83D\uDC1B"] });

        Assert.ThrowsAny<ArgumentException>(() => BugReportServiceOptions.Validate(Valid() with { Categories = ["\uD800"] }));
        Assert.ThrowsAny<ArgumentException>(() => BugReportServiceOptions.Validate(Valid() with { Categories = ["Bad\uDC1B"] }));
        Assert.ThrowsAny<ArgumentException>(() =>
            BugReportServiceOptions.Validate(Valid() with { Categories = ["Cut \uD83D"] }));
    }

    [Fact]
    public void Recording_budget_over_10_MiB_is_rejected()
    {
        const int TenMiB = 10 * 1024 * 1024;
        BugReportServiceOptions.Validate(Valid() with { Recording = new FrameHistoryOptions { MaxBytes = TenMiB } });

        ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(() =>
            BugReportServiceOptions.Validate(Valid() with { Recording = new FrameHistoryOptions { MaxBytes = TenMiB + 1 } }));
        Assert.Equal(nameof(FrameHistoryOptions.MaxBytes), refused.ParamName);
    }

    [Fact]
    public void Recording_longer_than_120_frames_is_rejected()
    {
        var options = Valid() with
        {
            Recording = new FrameHistoryOptions { Duration = TimeSpan.FromSeconds(10), FramesPerSecond = 30 },
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => BugReportServiceOptions.Validate(options));
        BugReportServiceOptions.Validate(Valid() with
        {
            Recording = new FrameHistoryOptions { Duration = TimeSpan.FromSeconds(4), FramesPerSecond = 30 },
        });
    }
}
