using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client.Sections;

public sealed class ClientSectionsTests
{
    static readonly BuildFacts Build = new("grimhollow", "Grimhollow", "0.12.3", null, "21.2.0", "Release", "3f2a9c1");

    static PlatformFacts Platform(IReadOnlyList<TelemetryHeaderValue> environment) => new(
        "macOS 27.0", "Arm64", "Arm64", ".NET 10.0.0", 12, 34_359_738_368L, "en-NZ",
        1280, 720, 2560, 1440, 2f, environment);

    static IBugReportSection[] BuiltIns() =>
    [
        new BuildSection(() => Build),
        new PlatformSection(() => Platform([])),
        new GraphicsSection(() => new GraphicsFacts("MetalNative", "Apple M3", 2560, 1440, "Vsync", null, null, null,
            new Dictionary<string, string>())),
        new PerformanceSection(new FrameStats(), new FrameTimeRing(), () => null),
        new LogSection(new RecentLogSink()),
        new SessionSection(() => new SessionFacts(TimeSpan.Zero, null, null, null)),
        new InputSection(() => new InputFacts([], [])),
    ];

    [Fact]
    public void Built_in_keys_and_budgets()
    {
        IBugReportSection[] sections = BuiltIns();
        Assert.Equal(
            new (string, int)[]
            {
                ("build", 4 * 1024), ("platform", 8 * 1024), ("graphics", 8 * 1024), ("performance", 32 * 1024),
                ("log", 256 * 1024), ("session", 4 * 1024), ("input", 4 * 1024),
            },
            sections.Select(section => (section.Key, section.MaxBytes)).ToArray());
        Assert.True(sections.Sum(section => section.MaxBytes) <= 400 * 1024);
    }

    [Fact]
    public void Build_section_writes_its_facts()
    {
        JsonElement json = SectionJsonReader.Write(new BuildSection(() => Build));

        Assert.Equal("grimhollow", json.GetProperty("gameId").GetString());
        Assert.Equal("Grimhollow", json.GetProperty("gameName").GetString());
        Assert.Equal("0.12.3", json.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("buildName").ValueKind);
        Assert.Equal("21.2.0", json.GetProperty("engineVersion").GetString());
        Assert.Equal("Release", json.GetProperty("configuration").GetString());
        Assert.Equal("3f2a9c1", json.GetProperty("commit").GetString());
    }

    [Fact]
    public void Performance_section_writes_the_last_300_frame_times()
    {
        var frames = new FrameTimeRing();
        for (int sample = 1; sample <= 400; sample++) frames.Add(sample);
        var stats = new FrameStats();
        stats.Sample(0.016f);

        JsonElement json = SectionJsonReader.Write(new PerformanceSection(stats, frames, () => null));

        float[] times = json.GetProperty("frameTimesMs").EnumerateArray().Select(value => value.GetSingle()).ToArray();
        Assert.Equal(300, times.Length);
        Assert.Equal(101f, times[0]);
        Assert.Equal(400f, times[^1]);
        Assert.Equal(stats.Fps, json.GetProperty("fps").GetSingle());
        Assert.Equal(stats.FrameMsAvg, json.GetProperty("frameMsAvg").GetSingle());
        Assert.Equal(stats.FrameMsMin, json.GetProperty("frameMsMin").GetSingle());
        Assert.Equal(stats.FrameMsMax, json.GetProperty("frameMsMax").GetSingle());
        Assert.True(json.GetProperty("managedBytes").GetInt64() > 0);
        Assert.Equal(0, json.GetProperty("passes").GetArrayLength());
    }

    [Fact]
    public void Performance_section_writes_each_pass_in_first_sampled_order()
    {
        var passes = new PassTimings(clock: new FixedClock());
        passes.Sample("shadow", 2f);
        passes.Sample("model", 5f);

        JsonElement json = SectionJsonReader.Write(new PerformanceSection(new FrameStats(), new FrameTimeRing(), () => passes));

        JsonElement[] rows = json.GetProperty("passes").EnumerateArray().ToArray();
        Assert.Equal(new[] { "shadow", "model" }, rows.Select(row => row.GetProperty("name").GetString()).ToArray());
        Assert.Equal(5f, rows[1].GetProperty("avgMs").GetSingle());
        Assert.Equal(5f, rows[1].GetProperty("minMs").GetSingle());
        Assert.Equal(5f, rows[1].GetProperty("maxMs").GetSingle());
    }

    [Fact]
    public void Log_section_writes_the_ring_lines()
    {
        var log = new RecentLogSink();
        log.Emit(new LogEntry(DateTimeOffset.UnixEpoch, LogLevel.Info, "Boot", "started"));
        log.Emit(new LogEntry(DateTimeOffset.UnixEpoch, LogLevel.Warn, "Net", "slow \"relay\""));

        JsonElement json = SectionJsonReader.Write(new LogSection(log));

        Assert.Equal(log.Snapshot(), json.GetProperty("lines").EnumerateArray().Select(line => line.GetString()!).ToArray());
    }

    [Fact]
    public void Log_section_keeps_the_newest_lines_that_fit_its_budget()
    {
        var log = new RecentLogSink();
        for (int i = 0; i < 500; i++)
            log.Emit(new LogEntry(DateTimeOffset.UnixEpoch, LogLevel.Info, "Spam", i + new string('x', 1000)));
        var section = new LogSection(log);

        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
            section.Write(writer, new BugReportContext("id", DateTimeOffset.UnixEpoch));
        using JsonDocument document = JsonDocument.Parse(output.WrittenMemory);

        string[] lines = document.RootElement.GetProperty("lines").EnumerateArray().Select(line => line.GetString()!).ToArray();
        IReadOnlyList<string> all = log.Snapshot();
        Assert.InRange(lines.Length, 1, all.Count - 1);
        Assert.Equal(all.Skip(all.Count - lines.Length), lines);
        Assert.True(output.WrittenCount <= section.MaxBytes, output.WrittenCount + " bytes");
    }

    [Fact]
    public void Log_section_keeps_the_newest_line_through_redaction_growth()
    {
        var log = new RecentLogSink();
        for (int i = 0; i < 500; i++)
            log.Emit(new LogEntry(DateTimeOffset.UnixEpoch, LogLevel.Info, "Spam", i + " pwd=a " + new string('x', 1000)));
        var set = new BugReportSectionSet();
        set.Add(new LogSection(log));

        IReadOnlyList<RawSection> raw = set.CaptureAll(new BugReportContext("id", DateTimeOffset.UnixEpoch));
        CapturedSection finished = Assert.Single(BugReportSectionSet.Finish(raw, new ReportRedactor(),
            BugReportSectionSet.DefaultFinishBudget));

        Assert.True(raw[0].Json.Length <= raw[0].MaxBytes * 9 / 10, raw[0].Json.Length + " raw bytes");
        using JsonDocument document = JsonDocument.Parse(finished.Json);
        string newest = document.RootElement.GetProperty("lines").EnumerateArray().Last().GetString()!;
        Assert.StartsWith("499 pwd=" + ReportRedactor.Mask, newest[newest.IndexOf("499", StringComparison.Ordinal)..],
            StringComparison.Ordinal);
    }

    [Fact]
    public void Platform_section_env_holds_only_what_it_was_given()
    {
        PlatformFacts facts = Platform([new("KE_GRAPHICS_BACKEND", "metal"), new("KE_TOKEN", ReportRedactor.Mask)]);

        JsonElement json = SectionJsonReader.Write(new PlatformSection(() => facts));

        Assert.Equal(
            new[] { ("KE_GRAPHICS_BACKEND", "metal"), ("KE_TOKEN", ReportRedactor.Mask) },
            json.GetProperty("env").EnumerateObject().Select(pair => (pair.Name, pair.Value.GetString()!)).ToArray());
        Assert.Equal("macOS 27.0", json.GetProperty("os").GetString());
        Assert.Equal("Arm64", json.GetProperty("osArchitecture").GetString());
        Assert.Equal("Arm64", json.GetProperty("processArchitecture").GetString());
        Assert.Equal(".NET 10.0.0", json.GetProperty("runtime").GetString());
        Assert.Equal(12, json.GetProperty("cpuCount").GetInt32());
        Assert.Equal(34_359_738_368L, json.GetProperty("totalMemoryBytes").GetInt64());
        Assert.Equal("en-NZ", json.GetProperty("locale").GetString());
        Assert.Equal(1280, json.GetProperty("window").GetProperty("width").GetInt32());
        Assert.Equal(720, json.GetProperty("window").GetProperty("height").GetInt32());
        Assert.Equal(2560, json.GetProperty("framebuffer").GetProperty("width").GetInt32());
        Assert.Equal(1440, json.GetProperty("framebuffer").GetProperty("height").GetInt32());
        Assert.Equal(2f, json.GetProperty("contentScale").GetSingle());
    }

    [Fact]
    public void Session_section_writes_file_name_not_path()
    {
        var crashAt = new DateTimeOffset(2026, 10, 17, 3, 4, 5, 678, TimeSpan.FromHours(13));
        var facts = new SessionFacts(TimeSpan.FromSeconds(90.5), "/Users/ann/logs/session-1.log",
            @"C:\Users\ann\AppData\Local\KhaozEngine\crash\game-crash-1.log", crashAt);

        JsonElement json = SectionJsonReader.Write(new SessionSection(() => facts));

        Assert.Equal(90.5, json.GetProperty("uptimeSeconds").GetDouble());
        Assert.Equal("session-1.log", json.GetProperty("sessionLog").GetString());
        JsonElement crash = json.GetProperty("previousCrash");
        Assert.Equal("game-crash-1.log", crash.GetProperty("file").GetString());
        Assert.Equal("2026-10-16T14:04:05.678+00:00", crash.GetProperty("writtenAt").GetString());
    }

    [Fact]
    public void Session_section_without_a_previous_crash_writes_null()
    {
        JsonElement json = SectionJsonReader.Write(new SessionSection(() => new SessionFacts(TimeSpan.Zero, null, null, null)));

        Assert.Equal(JsonValueKind.Null, json.GetProperty("sessionLog").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("previousCrash").ValueKind);
    }

    [Fact]
    public void Graphics_capabilities_are_an_object()
    {
        var facts = new GraphicsFacts("MetalNative", "Apple M3", 2560, 1440, "Vsync", false, null, null,
            new Dictionary<string, string> { ["SupportsCompute"] = "true", ["MaxMsaaSampleCount"] = "8" });

        JsonElement json = SectionJsonReader.Write(new GraphicsSection(() => facts));

        JsonElement capabilities = json.GetProperty("capabilities");
        Assert.Equal(JsonValueKind.Object, capabilities.ValueKind);
        Assert.Equal("true", capabilities.GetProperty("SupportsCompute").GetString());
        Assert.Equal("8", capabilities.GetProperty("MaxMsaaSampleCount").GetString());
        Assert.Equal("MetalNative", json.GetProperty("backend").GetString());
        Assert.Equal("Apple M3", json.GetProperty("adapter").GetString());
        Assert.Equal(2560, json.GetProperty("swapchain").GetProperty("width").GetInt32());
        Assert.Equal(1440, json.GetProperty("swapchain").GetProperty("height").GetInt32());
        Assert.Equal("Vsync", json.GetProperty("presentMode").GetString());
        Assert.False(json.GetProperty("softwareAdapter").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("deviceLossReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("injectedModules").ValueKind);
    }

    [Fact]
    public void Graphics_section_lists_injected_modules()
    {
        var facts = new GraphicsFacts("Direct3D11Native", "GPU", 1, 1, "Immediate", null, "removed", ["overlay.dll"],
            new Dictionary<string, string>());

        JsonElement json = SectionJsonReader.Write(new GraphicsSection(() => facts));

        Assert.Equal("removed", json.GetProperty("deviceLossReason").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("softwareAdapter").ValueKind);
        Assert.Equal(new[] { "overlay.dll" },
            json.GetProperty("injectedModules").EnumerateArray().Select(module => module.GetString()).ToArray());
    }

    [Fact]
    public void Graphics_section_bounds_an_oversized_adapter_and_loss_reason()
    {
        var facts = new GraphicsFacts("MetalNative", new string('a', 5000), 1, 1, "Vsync", null, new string('r', 1025), null,
            new Dictionary<string, string>());

        JsonElement json = SectionJsonReader.Write(new GraphicsSection(() => facts));

        Assert.Equal(new string('a', 1021) + "...", json.GetProperty("adapter").GetString());
        Assert.Equal(new string('r', 1021) + "...", json.GetProperty("deviceLossReason").GetString());
    }

    [Fact]
    public void Graphics_section_caps_its_lists_and_bounds_each_entry()
    {
        var capabilities = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < 200; i++) capabilities["Cap" + i.ToString("D3", CultureInfo.InvariantCulture)] = "v";
        capabilities["Cap000"] = new string('v', 3000);
        capabilities["Cap000" + new string('k', 3000)] = "x";
        var modules = Enumerable.Range(0, 100).Select(i => "m" + i.ToString(CultureInfo.InvariantCulture)).ToList();
        modules[0] = new string('m', 2000);
        var facts = new GraphicsFacts("MetalNative", "GPU", 1, 1, "Vsync", null, null, modules, capabilities);

        JsonElement json = SectionJsonReader.Write(new GraphicsSection(() => facts));

        JsonProperty[] written = json.GetProperty("capabilities").EnumerateObject().ToArray();
        Assert.Equal(128, written.Length);
        Assert.Equal(new string('v', 1021) + "...", written[0].Value.GetString());
        Assert.Equal("Cap000" + new string('k', 1015) + "...", written[1].Name);
        Assert.All(written, property => Assert.InRange(property.Name.Length, 1, 1024));
        string[] writtenModules = json.GetProperty("injectedModules").EnumerateArray().Select(module => module.GetString()!).ToArray();
        Assert.Equal(64, writtenModules.Length);
        Assert.Equal(new string('m', 1021) + "...", writtenModules[0]);
        Assert.Equal("m63", writtenModules[^1]);
    }

    [Fact]
    public void Platform_section_bounds_its_strings_and_caps_its_environment()
    {
        var environment = new List<TelemetryHeaderValue>
        {
            new("KE_LONG_VALUE", new string('x', 4000)),
            new("KE_" + new string('N', 2000), "named"),
        };
        for (int i = 0; i < 100; i++) environment.Add(new("KE_VAR_" + i.ToString(CultureInfo.InvariantCulture), "v"));
        PlatformFacts facts = Platform(environment) with { Os = new string('o', 1500), Runtime = new string('t', 1500) };

        JsonElement json = SectionJsonReader.Write(new PlatformSection(() => facts));

        Assert.Equal(new string('o', 1021) + "...", json.GetProperty("os").GetString());
        Assert.Equal(new string('t', 1021) + "...", json.GetProperty("runtime").GetString());
        JsonProperty[] env = json.GetProperty("env").EnumerateObject().ToArray();
        Assert.Equal(64, env.Length);
        Assert.Equal(new string('x', 1021) + "...", env[0].Value.GetString());
        Assert.Equal("KE_" + new string('N', 1018) + "...", env[1].Name);
        Assert.Equal("KE_VAR_61", env[^1].Name);
    }

    [Fact]
    public void Input_section_writes_devices_and_trigger_bindings()
    {
        var facts = new InputFacts(["keyboard", "mouse", "gamepad 0"], ["Key:F10", "GamepadButton:Back"]);

        JsonElement json = SectionJsonReader.Write(new InputSection(() => facts));

        Assert.Equal(facts.Devices, json.GetProperty("devices").EnumerateArray().Select(device => device.GetString()!).ToArray());
        Assert.Equal(facts.TriggerBindings,
            json.GetProperty("trigger").EnumerateArray().Select(binding => binding.GetString()!).ToArray());
    }

    sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.UnixEpoch;
    }
}
