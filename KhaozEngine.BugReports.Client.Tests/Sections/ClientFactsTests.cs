using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.BugReports.Client;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using KhaozEngine.Windowing.Actions;
using Xunit;

[assembly: AssemblyMetadata("TestGame.BuildName", "Stone")]

namespace KhaozEngine.Tests.BugReports.Client.Sections;

public sealed class ClientFactsTests
{
    static readonly Assembly TestAssembly = typeof(ClientFactsTests).Assembly;
    static readonly DateTimeOffset FixedStart = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_facts_read_product_version_and_commit()
    {
        string informational = TestAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        int plus = informational.IndexOf('+');

        BuildFacts facts = ClientFacts.Build(TestAssembly, "grimhollow", "Grimhollow");

        Assert.Equal("grimhollow", facts.GameId);
        Assert.Equal("Grimhollow", facts.GameName);
        Assert.Equal(plus < 0 ? informational : informational[..plus], facts.Version);
        Assert.Equal(plus < 0 ? null : informational[(plus + 1)..], facts.Commit);
        Assert.Equal(TelemetrySessionHeader.EngineVersion, facts.EngineVersion);
        Assert.Equal(TestAssembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration, facts.Configuration);
    }

    [Fact]
    public void Build_facts_read_the_build_name_metadata()
        => Assert.Equal("Stone", ClientFacts.Build(TestAssembly, "test", "Test").BuildName);

    [Fact]
    public void Build_facts_without_an_assembly_are_unknown()
    {
        BuildFacts facts = ClientFacts.Build(null, "test", "Test");

        Assert.False(string.IsNullOrEmpty(facts.Version));
        Assert.Equal(TelemetrySessionHeader.EngineVersion, facts.EngineVersion);
    }

    [Fact]
    public void Previous_crash_is_the_newest_file_before_process_start()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ke-crash-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var start = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
            Touch(directory, "game-crash-20261017-090000-000-1-0.log", start.AddHours(-3));
            Touch(directory, "game-crash-20261017-110000-000-1-0.log", start.AddHours(-1));
            Touch(directory, "game-crash-20261017-120100-000-2-0.log", start.AddMinutes(1));
            Touch(directory, "game-taskfault-20261017-115000-000-1-0.log", start.AddMinutes(-10));
            Touch(directory, "session.log", start.AddMinutes(-5));

            (string? file, DateTimeOffset? at) = ClientFacts.FindPreviousCrash(directory, start);

            Assert.Equal("game-crash-20261017-110000-000-1-0.log", Path.GetFileName(file));
            Assert.Equal(start.AddHours(-1), at);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Previous_crash_in_a_missing_directory_is_none()
    {
        string missing = Path.Combine(Path.GetTempPath(), "ke-crash-missing-" + Guid.NewGuid().ToString("N"));

        Assert.Equal((null, null), ClientFacts.FindPreviousCrash(missing, FixedStart));
    }

    [Fact]
    public void Bindings_describe_key_and_gamepad_sources()
    {
        var map = new ActionMap();
        map.AddAction(InputAction.Button("ReportBug"), InputSource.FromKey(Key.F10), InputSource.FromGamepadButton(GamepadButton.Back));

        Assert.Equal(new[] { "Key:F10", "GamepadButton:Back" }, ClientFacts.DescribeBindings(map, "ReportBug"));
    }

    [Fact]
    public void Bindings_describe_every_source_kind()
    {
        var map = new ActionMap();
        map.AddAction(InputAction.Button("Any"),
            InputSource.FromMouseButton(MouseButton.Right),
            InputSource.StickAxis(GamepadStick.Left, StickComponent.Y),
            InputSource.WholeStick(GamepadStick.Right),
            InputSource.Trigger(GamepadTriggerSide.Right),
            InputSource.Axis1D(Key.Q, Key.E),
            InputSource.WasdDefault,
            InputSource.None);

        Assert.Equal(
            new[]
            {
                "MouseButton:Right", "GamepadStickAxis:Left.Y", "GamepadStickAxis:Right.Both", "GamepadTrigger:Right",
                "KeyAxis1D:Q,E", "KeyAxis2D:A,D,S,W",
            },
            ClientFacts.DescribeBindings(map, "Any"));
    }

    [Fact]
    public void Bindings_of_an_undeclared_action_are_empty()
        => Assert.Empty(ClientFacts.DescribeBindings(new ActionMap(), "ReportBug"));

    [Fact]
    public void Devices_list_keyboard_mouse_and_each_connected_pad()
    {
        var input = new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(), Vector2.Zero, Vector2.Zero, 0f, 1, 1,
            gamepads: [Pad(0), GamepadState.Disconnected, Pad(2)]);

        Assert.Equal(new[] { "keyboard", "mouse", "gamepad 0", "gamepad 2" }, ClientFacts.Devices(input));
    }

    [Fact]
    public void Capabilities_are_every_bool_int_and_string_property_by_name()
    {
        var capabilities = new GpuCapabilities(clipSpaceYInverted: true, depthRangeZeroToOne: true, deviceName: "Apple M3",
            maxMsaaSampleCount: 4, supportsCompute: true)
        { MaxFragmentStorageBuffers = 7 };

        IReadOnlyDictionary<string, string> described = ClientFacts.DescribeCapabilities(capabilities);

        Assert.Equal(
            new[]
            {
                "ClipSpaceYInverted", "DepthRangeZeroToOne", "DeviceName", "MaxColorAttachments", "MaxFragmentStorageBuffers",
                "MaxMsaaSampleCount",
                "SamplerAnisotropy", "SamplerLodBias", "SupportsCompletionFences", "SupportsCompute",
                "SupportsShadowMaps", "SupportsTimestamps",
            },
            described.Keys);
        Assert.Equal("true", described["ClipSpaceYInverted"]);
        Assert.Equal("false", described["SamplerLodBias"]);
        Assert.Equal("Apple M3", described["DeviceName"]);
        Assert.Equal("4", described["MaxMsaaSampleCount"]);
        Assert.Equal("7", described["MaxFragmentStorageBuffers"]);
    }

    [Fact]
    public void Captured_invariants_are_this_process_facts()
    {
        var redactor = new ReportRedactor();
        PlatformInvariants invariants = ClientFacts.CaptureInvariants(redactor);

        Assert.Equal(RuntimeInformation.OSDescription, invariants.Os);
        Assert.Equal(RuntimeInformation.OSArchitecture.ToString(), invariants.OsArchitecture);
        Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), invariants.ProcessArchitecture);
        Assert.Equal(RuntimeInformation.FrameworkDescription, invariants.Runtime);
        Assert.Equal(Environment.ProcessorCount, invariants.CpuCount);
        Assert.True(invariants.TotalMemoryBytes > 0);
        Assert.Equal(CultureInfo.CurrentCulture.Name, invariants.Locale);
        var variables = new List<KeyValuePair<string, string?>>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            variables.Add(new KeyValuePair<string, string?>((string)entry.Key, entry.Value as string));
        Assert.Equal(redactor.SelectEnvironment(variables), invariants.Environment);
    }

    [Theory]
    [InlineData(2560, 1440, 2f, 1280, 720)]
    [InlineData(1920, 1080, 1f, 1920, 1080)]
    [InlineData(1001, 501, 2f, 501, 251)]
    [InlineData(2880, 1620, 1.5f, 1920, 1080)]
    public void Platform_sizes_come_from_the_trigger_frame(int framebufferWidth, int framebufferHeight, float scale,
        int windowWidth, int windowHeight)
    {
        TelemetryHeaderValue[] environment = [new("KE_GRAPHICS_BACKEND", "metal")];
        var invariants = new PlatformInvariants("Linux 6.8", "X64", "X64", ".NET 10.0.0", 8, 1024L, "en-NZ", environment);
        var frame = new InputState(Vector2.Zero, new Vector2(scale, scale), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<Key>(), new HashSet<MouseButton>(), new HashSet<MouseButton>(), Vector2.Zero, Vector2.Zero, 0f,
            framebufferWidth, framebufferHeight);

        PlatformFacts facts = ClientFacts.Platform(invariants, frame, ui: null);

        Assert.Equal(("Linux 6.8", "X64", "X64", ".NET 10.0.0", 8, 1024L, "en-NZ"),
            (facts.Os, facts.OsArchitecture, facts.ProcessArchitecture, facts.Runtime, facts.CpuCount, facts.TotalMemoryBytes,
                facts.Locale));
        Assert.Equal((windowWidth, windowHeight), (facts.WindowWidth, facts.WindowHeight));
        Assert.Equal((framebufferWidth, framebufferHeight), (facts.FramebufferWidth, facts.FramebufferHeight));
        Assert.Equal(scale, facts.ContentScale);
        // The trigger frame reuses the environment selected at startup and never reads the process environment.
        Assert.Same(environment, facts.Environment);
    }

    [Fact]
    public void Platform_content_scale_is_the_ui_dpi_scale()
    {
        var invariants = new PlatformInvariants("Linux 6.8", "X64", "X64", ".NET 10.0.0", 8, 1024L, "en-NZ", []);
        var frame = new InputState(Vector2.Zero, new Vector2(2f, 2f), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<Key>(), new HashSet<MouseButton>(), new HashSet<MouseButton>(), Vector2.Zero, Vector2.Zero, 0f,
            2560, 1440);
        var ui = new UiViewport(3000, 1500, 2000, 1000);

        PlatformFacts facts = ClientFacts.Platform(invariants, frame, ui);

        Assert.Equal(1.5f, facts.ContentScale);
        // The window size still follows the trigger frame's framebuffer scale.
        Assert.Equal((1280, 720), (facts.WindowWidth, facts.WindowHeight));
    }

    static void Touch(string directory, string name, DateTimeOffset writtenAt)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, writtenAt.UtcDateTime);
    }

    static GamepadState Pad(int index) => new(index, new HashSet<GamepadButton>(), new HashSet<GamepadButton>(),
        new HashSet<GamepadButton>(), Vector2.Zero, Vector2.Zero, 0f, 0f);
}
