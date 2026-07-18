using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

/// <summary>
/// Covers the updater progress-window seam that is headless-testable: the phase/progress reporting the
/// applier drives into an <see cref="IUpdaterUi"/>, the config round-trip of the optional UI block
/// through the source-generated JSON context, and the theme resolution from that config. The native
/// Win32 window itself is verified manually on a real Windows box (it draws nothing off Windows).
/// </summary>
public sealed class UpdaterUiTests
{
    private const string Install = "/install";
    private const string Staging = "/staging";
    private const string AppData = "/appdata";

    private static string InstallPath(string rel) => Path.Combine(Install, rel.Replace('/', Path.DirectorySeparatorChar));
    private static string StagingPath(string rel) => Path.Combine(Staging, rel.Replace('/', Path.DirectorySeparatorChar));

    private static ApplyUpdateConfig Config(List<string> copy, ApplyUpdateUiConfig? ui = null)
        => new()
        {
            TargetVersion = "2.0.0",
            InstallDir = Install,
            StagingDir = Staging,
            FilesToCopy = copy,
            GameExePath = InstallPath("Game"),
            ParentPid = 1234,
            ManifestDestPath = Path.Combine(AppData, "update-manifest.json"),
            Ui = ui,
        };

    [Fact]
    public void Apply_ReportsInstallThenFinishingPhases_WithProgressPerFile()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("a.dll")] = "a";
        env.Files[StagingPath("b.dll")] = "b";
        env.Files[InstallPath("Game")] = "exe";
        var ui = new RecordingUpdaterUi();

        ApplyResult result = UpdateApplier.Apply(Config(new() { "a.dll", "b.dll" }), env, ui);

        Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, ui.ShowCalls);
        // Phase order: Install while copying, Finishing during the settle wait.
        Assert.Equal(new[] { UpdaterPhase.Install, UpdaterPhase.Finishing }, ui.Phases);
        // One progress tick per copied file, monotonically completing the total.
        Assert.Equal(new[] { (1, 2), (2, 2) }, ui.Progress);
        // The window is closed exactly once (right before relaunch, via the Relaunch helper).
        Assert.Equal(1, ui.CloseCalls);
        // The last status shown is the Finishing text.
        Assert.Equal(ui.ShownTheme!.FinishingText, ui.Statuses[^1]);
    }

    [Fact]
    public void Apply_UsesThemedStatusStrings_FromConfigUiBlock()
    {
        var env = new FakeUpdaterEnvironment();
        env.Files[StagingPath("a.dll")] = "a";
        env.Files[InstallPath("Game")] = "exe";
        var ui = new RecordingUpdaterUi();
        var uiConfig = new ApplyUpdateUiConfig
        {
            InstallingText = "Installing Nullwake",
            FinishingText = "Almost there",
        };

        UpdateApplier.Apply(Config(new() { "a.dll" }, uiConfig), env, ui);

        Assert.Contains("Installing Nullwake", ui.Statuses);
        Assert.Contains("Almost there", ui.Statuses);
    }

    [Fact]
    public void ApplyUpdateConfig_WithUiBlock_RoundTripsThroughSourceGenContext()
    {
        var config = new ApplyUpdateConfig
        {
            TargetVersion = "3.1.0",
            InstallDir = "/install",
            Ui = new ApplyUpdateUiConfig
            {
                WindowTitle = "Nullwake",
                Heading = "Updating Nullwake",
                Accent = new UpdaterUiColor { R = 120, G = 200, B = 255 },
                Background = new UpdaterUiColor { R = 10, G = 14, B = 20 },
                Text = new UpdaterUiColor { R = 235, G = 238, B = 245 },
                LogoPath = "assets/logo.png",
                InstallingText = "Installing update",
                FinishingText = "Finishing up...",
                DownloadingText = "Downloading",
            },
        };

        string json = JsonSerializer.Serialize(config, UpdatesJsonContext.Default.ApplyUpdateConfig);
        ApplyUpdateConfig? back = JsonSerializer.Deserialize(json, UpdatesJsonContext.Default.ApplyUpdateConfig);

        Assert.NotNull(back);
        Assert.NotNull(back!.Ui);
        Assert.Equal("Nullwake", back.Ui!.WindowTitle);
        Assert.Equal("Updating Nullwake", back.Ui.Heading);
        Assert.Equal(120, back.Ui.Accent!.R);
        Assert.Equal(200, back.Ui.Accent.G);
        Assert.Equal(255, back.Ui.Accent.B);
        Assert.Equal(10, back.Ui.Background!.R);
        Assert.Equal("assets/logo.png", back.Ui.LogoPath);
        Assert.Equal("Installing update", back.Ui.InstallingText);
        Assert.Equal("Finishing up...", back.Ui.FinishingText);
        Assert.Equal("Downloading", back.Ui.DownloadingText);
    }

    [Fact]
    public void ApplyUpdateConfig_WithoutUiBlock_RoundTripsAsNull()
    {
        var config = new ApplyUpdateConfig { TargetVersion = "3.1.0", InstallDir = "/install" };

        string json = JsonSerializer.Serialize(config, UpdatesJsonContext.Default.ApplyUpdateConfig);
        ApplyUpdateConfig? back = JsonSerializer.Deserialize(json, UpdatesJsonContext.Default.ApplyUpdateConfig);

        Assert.NotNull(back);
        Assert.Null(back!.Ui);
    }

    [Fact]
    public void UpdaterUiTheme_FromNullConfig_IsAllDefaults()
    {
        var defaults = new UpdaterUiTheme();
        UpdaterUiTheme theme = UpdaterUiTheme.FromConfig(null, "/install");

        Assert.Equal(defaults.WindowTitle, theme.WindowTitle);
        Assert.Equal(defaults.Accent, theme.Accent);
        Assert.Equal(defaults.FinishingText, theme.FinishingText);
        Assert.Null(theme.LogoPath);
    }

    [Fact]
    public void UpdaterUiTheme_FromConfig_OverridesSetFields_AndResolvesLogoUnderInstall()
    {
        var ui = new ApplyUpdateUiConfig
        {
            WindowTitle = "Nullwake",
            Accent = new UpdaterUiColor { R = 120, G = 200, B = 255 },
            LogoPath = "assets/logo.png",
            InstallingText = "Installing Nullwake",
        };

        UpdaterUiTheme theme = UpdaterUiTheme.FromConfig(ui, "/games/nullwake");

        Assert.Equal("Nullwake", theme.WindowTitle);
        // Heading defaults to the window title when unset.
        Assert.Equal("Nullwake", theme.Heading);
        Assert.Equal(((byte)120, (byte)200, (byte)255), theme.Accent);
        Assert.Equal("Installing Nullwake", theme.InstallingText);
        // Finishing text stays the default (not overridden).
        Assert.Equal(new UpdaterUiTheme().FinishingText, theme.FinishingText);
        // Logo resolved to an absolute path under the install dir.
        Assert.Equal(Path.Combine("/games/nullwake", "assets".Replace('/', Path.DirectorySeparatorChar), "logo.png"), theme.LogoPath);
    }

    [Fact]
    public void NullUpdaterUi_AllMethodsAreNoOps()
    {
        IUpdaterUi ui = NullUpdaterUi.Instance;
        // Just exercising the surface: none of these throw and there is nothing to observe.
        ui.Show(new UpdaterUiTheme());
        ui.SetPhase(UpdaterPhase.Install);
        ui.SetProgress(1, 2);
        ui.SetStatus("x");
        ui.Close();
        ui.Close();
    }
}
