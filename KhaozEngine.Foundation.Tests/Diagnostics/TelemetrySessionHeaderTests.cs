using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Diagnostics;

/// <summary>
/// The self-identifying first line of a telemetry recording: that it is first, that it is distinguishable from
/// a sample row, that it carries the engine-owned identity, that only <c>KE_</c> environment levers reach it,
/// and that the consumer's own durable values land in their own section.
/// </summary>
public sealed class TelemetrySessionHeaderTests
{
    static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "ke-telemetry-tests", Guid.NewGuid().ToString("N") + ".jsonl");

    static string[] ReadLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        string? line;
        while ((line = sr.ReadLine()) != null)
            if (line.Length > 0) lines.Add(line);
        return lines.ToArray();
    }

    static IReadOnlyList<TelemetryHeaderValue> NoEnvironment() => Array.Empty<TelemetryHeaderValue>();

    static JsonElement Session(string headerLine)
    {
        // Parsed into a clone so the caller can use it after the JsonDocument is disposed.
        using JsonDocument doc = JsonDocument.Parse(headerLine);
        return doc.RootElement.GetProperty("session").Clone();
    }

    [Fact]
    public void Header_is_the_first_line_and_the_only_line_without_t()
    {
        string path = TempPath();
        var rec = new TelemetryRecorder();
        rec.Start(path, new TelemetrySessionInfo { AppName = "Ruinborne" });
        rec.Sample(0.0, new[] { new TelemetryChannel("fps", 60.0) });
        rec.Sample(0.5, new[] { new TelemetryChannel("fps", 59.0) });
        rec.Stop();

        string[] lines = ReadLines(path);
        Assert.Equal(3, lines.Length);

        using JsonDocument header = JsonDocument.Parse(lines[0]);
        Assert.True(header.RootElement.TryGetProperty("session", out _));
        Assert.False(header.RootElement.TryGetProperty("t", out _));

        for (int i = 1; i < lines.Length; i++)
        {
            using JsonDocument row = JsonDocument.Parse(lines[i]);
            Assert.True(row.RootElement.TryGetProperty("t", out _));
            Assert.False(row.RootElement.TryGetProperty("session", out _));
        }
    }

    [Fact]
    public void Per_frame_rows_are_unchanged_by_the_header()
    {
        string path = TempPath();
        var rec = new TelemetryRecorder();
        rec.Start(path, new TelemetrySessionInfo { AppName = "Ruinborne" });
        rec.Sample(1.25, new[] { new TelemetryChannel("fps", 59.7), new TelemetryChannel("rttMs", 48) });
        rec.Stop();

        string[] lines = ReadLines(path);
        using JsonDocument row = JsonDocument.Parse(lines[1]);
        Assert.Equal(1.25, row.RootElement.GetProperty("t").GetDouble());
        Assert.Equal(59.7, row.RootElement.GetProperty("fps").GetDouble(), 3);
        Assert.Equal(48, row.RootElement.GetProperty("rttMs").GetDouble());
    }

    [Fact]
    public void A_recording_with_no_session_info_still_opens_with_a_header()
    {
        string path = TempPath();
        var rec = new TelemetryRecorder();
        rec.Start(path);       // the no-options overload
        rec.Stop();            // and no samples at all

        string[] lines = ReadLines(path);
        Assert.Single(lines);

        JsonElement session = Session(lines[0]);
        Assert.Equal(TelemetrySessionHeader.SchemaVersion, session.GetProperty("v").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(session.GetProperty("engine").GetString()));
        Assert.Equal(JsonValueKind.Null, session.GetProperty("app").GetProperty("name").ValueKind);
    }

    [Fact]
    public void Header_carries_the_schema_marker_and_the_engine_informational_version()
    {
        JsonElement session = Session(TelemetrySessionHeader.Build(null, NoEnvironment()));

        Assert.Equal(1, TelemetrySessionHeader.SchemaVersion);
        Assert.Equal(1, session.GetProperty("v").GetInt32());

        string? expected = typeof(TelemetryRecorder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.Equal(expected, session.GetProperty("engine").GetString());
        Assert.Equal(expected, TelemetrySessionHeader.EngineVersion);
    }

    [Fact]
    public void Header_carries_every_expected_field()
    {
        JsonElement session = Session(TelemetrySessionHeader.Build(null, NoEnvironment()));

        Assert.True(session.TryGetProperty("v", out _));
        Assert.True(session.TryGetProperty("engine", out _));

        JsonElement app = session.GetProperty("app");
        foreach (string field in new[] { "name", "version", "build" })
            Assert.True(app.TryGetProperty(field, out _), $"app.{field} missing");

        JsonElement gpu = session.GetProperty("gpu");
        foreach (string field in new[]
                 {
                     "backend", "backendSource", "requestedBackend", "requestedOverride", "adapter",
                     "injectedModules", "threading",
                 })
            Assert.True(gpu.TryGetProperty(field, out _), $"gpu.{field} missing");

        Assert.Equal(JsonValueKind.Object, session.GetProperty("env").ValueKind);
        Assert.Equal(JsonValueKind.Object, session.GetProperty("game").ValueKind);
    }

    [Fact]
    public void Header_carries_the_app_identity_handed_in_by_the_consumer()
    {
        var info = new TelemetrySessionInfo
        {
            AppName = "Ruinborne",
            AppVersion = "0.7.3",
            BuildName = "Sundered Ground",
        };

        JsonElement app = Session(TelemetrySessionHeader.Build(info, NoEnvironment())).GetProperty("app");
        Assert.Equal("Ruinborne", app.GetProperty("name").GetString());
        Assert.Equal("0.7.3", app.GetProperty("version").GetString());
        Assert.Equal("Sundered Ground", app.GetProperty("build").GetString());
    }

    [Fact]
    public void Blank_app_identity_reads_as_null_rather_than_an_empty_string()
    {
        var info = new TelemetrySessionInfo { AppName = "   ", AppVersion = "", BuildName = null };

        JsonElement app = Session(TelemetrySessionHeader.Build(info, NoEnvironment())).GetProperty("app");
        Assert.Equal(JsonValueKind.Null, app.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, app.GetProperty("version").ValueKind);
        Assert.Equal(JsonValueKind.Null, app.GetProperty("build").ValueKind);
    }

    [Fact]
    public void Header_carries_the_gpu_facts_and_the_direct3d11_threading_caps()
    {
        var info = new TelemetrySessionInfo
        {
            GpuBackend = "Direct3D11",
            GpuBackendSource = "FallbackAfterFailure",
            AdapterDescription = "NVIDIA GeForce RTX 4070",
            InjectedModules = new[] { "RTSSHooks64.dll" },
            DriverCommandLists = false,
            DriverConcurrentCreates = true,
        };

        JsonElement gpu = Session(TelemetrySessionHeader.Build(info, NoEnvironment())).GetProperty("gpu");
        Assert.Equal("Direct3D11", gpu.GetProperty("backend").GetString());
        Assert.Equal("FallbackAfterFailure", gpu.GetProperty("backendSource").GetString());
        Assert.Equal("NVIDIA GeForce RTX 4070", gpu.GetProperty("adapter").GetString());

        JsonElement modules = gpu.GetProperty("injectedModules");
        Assert.Equal(JsonValueKind.Array, modules.ValueKind);
        Assert.Equal("RTSSHooks64.dll", modules[0].GetString());

        JsonElement threading = gpu.GetProperty("threading");
        Assert.False(threading.GetProperty("driverCommandLists").GetBoolean());
        Assert.True(threading.GetProperty("driverConcurrentCreates").GetBoolean());
    }

    [Fact]
    public void A_fallback_records_what_was_asked_for_and_not_only_that_it_fell_back()
    {
        // The whole point of the two requested fields: without them this capture says a fallback happened and
        // cannot say what failed, which is strictly less than the session log beside it already carries.
        var info = new TelemetrySessionInfo
        {
            GpuBackend = "Direct3D11",
            GpuBackendSource = "FallbackAfterFailure",
            GpuRequestedBackend = "Vulkan",
            GpuRequestedOverride = "vulkan",
        };

        JsonElement gpu = Session(TelemetrySessionHeader.Build(info, NoEnvironment())).GetProperty("gpu");
        Assert.Equal("Direct3D11", gpu.GetProperty("backend").GetString());
        Assert.Equal("FallbackAfterFailure", gpu.GetProperty("backendSource").GetString());
        Assert.Equal("Vulkan", gpu.GetProperty("requestedBackend").GetString());
        Assert.Equal("vulkan", gpu.GetProperty("requestedOverride").GetString());
    }

    [Fact]
    public void An_ordinary_selection_records_both_requested_fields_as_null()
    {
        var info = new TelemetrySessionInfo { GpuBackend = "Metal", GpuBackendSource = "OsProbe" };

        JsonElement gpu = Session(TelemetrySessionHeader.Build(info, NoEnvironment())).GetProperty("gpu");
        Assert.Equal(JsonValueKind.Null, gpu.GetProperty("requestedBackend").ValueKind);
        Assert.Equal(JsonValueKind.Null, gpu.GetProperty("requestedOverride").ValueKind);
    }

    [Fact]
    public void The_raw_requested_override_is_recorded_untouched()
    {
        // Deliberately NOT normalized: the untouched string is what makes a typo or stray quoting obvious, so
        // it must survive surrounding whitespace and odd casing exactly as it was read.
        var info = new TelemetrySessionInfo { GpuRequestedOverride = " Vulcan \"" };

        JsonElement gpu = Session(TelemetrySessionHeader.Build(info, NoEnvironment())).GetProperty("gpu");
        Assert.Equal(" Vulcan \"", gpu.GetProperty("requestedOverride").GetString());
    }

    [Fact]
    public void Threading_caps_are_null_when_the_backend_never_reported_them()
    {
        var info = new TelemetrySessionInfo { GpuBackend = "Metal" };

        JsonElement gpu = Session(TelemetrySessionHeader.Build(info, NoEnvironment())).GetProperty("gpu");
        Assert.Equal(JsonValueKind.Null, gpu.GetProperty("threading").ValueKind);
    }

    [Fact]
    public void An_unscanned_module_list_is_null_and_a_clean_scan_is_an_empty_array()
    {
        // Opposite facts: "we never looked" versus "we looked and it was clean".
        JsonElement unscanned = Session(TelemetrySessionHeader.Build(
            new TelemetrySessionInfo { InjectedModules = null }, NoEnvironment())).GetProperty("gpu");
        Assert.Equal(JsonValueKind.Null, unscanned.GetProperty("injectedModules").ValueKind);

        JsonElement clean = Session(TelemetrySessionHeader.Build(
            new TelemetrySessionInfo { InjectedModules = Array.Empty<string>() }, NoEnvironment())).GetProperty("gpu");
        JsonElement modules = clean.GetProperty("injectedModules");
        Assert.Equal(JsonValueKind.Array, modules.ValueKind);
        Assert.Equal(0, modules.GetArrayLength());
    }

    [Fact]
    public void Only_KE_prefixed_variables_are_selected_and_they_are_sorted_by_name()
    {
        IReadOnlyList<TelemetryHeaderValue> levers = TelemetrySessionHeader.SelectEngineVariables(new[]
        {
            new KeyValuePair<string, string?>("KE_GRAPHICS_BACKEND", "vulkan"),
            new KeyValuePair<string, string?>("PATH", "/usr/bin"),
            new KeyValuePair<string, string?>("KE_D3D11_PREVENT_THREADING_OPTIMIZATIONS", "1"),
            new KeyValuePair<string, string?>("HOME", "/home/tester"),
            new KeyValuePair<string, string?>("SOMETHING_KE_NOT_A_PREFIX", "no"),
        });

        Assert.Equal(2, levers.Count);
        Assert.Equal("KE_D3D11_PREVENT_THREADING_OPTIMIZATIONS", levers[0].Key);
        Assert.Equal("1", levers[0].Value);
        Assert.Equal("KE_GRAPHICS_BACKEND", levers[1].Key);
        Assert.Equal("vulkan", levers[1].Value);
    }

    [Fact]
    public void The_prefix_match_is_case_insensitive_and_that_is_pinned()
    {
        // Deliberate: on a host where env names are case-insensitive, a lever typed in the wrong case still
        // resolves and still shaped the run, so a capture that dropped it would be lying by omission. Pinned
        // here so tightening this to Ordinal (or loosening it further) cannot land silently.
        IReadOnlyList<TelemetryHeaderValue> levers = TelemetrySessionHeader.SelectEngineVariables(new[]
        {
            new KeyValuePair<string, string?>("ke_graphics_backend", "vulkan"),
            new KeyValuePair<string, string?>("Ke_Mixed_Case", "1"),
            new KeyValuePair<string, string?>("NOT_KE_PREFIXED", "no"),
        });

        Assert.Equal(2, levers.Count);
        Assert.Contains(levers, l => l.Key == "ke_graphics_backend");
        Assert.Contains(levers, l => l.Key == "Ke_Mixed_Case");
        Assert.DoesNotContain(levers, l => l.Key == "NOT_KE_PREFIXED");
    }

    [Fact]
    public void A_live_recording_records_the_set_KE_lever_and_nothing_else_from_the_environment()
    {
        const string lever = "KE_FAKE_TELEMETRY_HEADER_LEVER";
        const string other = "FAKE_TELEMETRY_HEADER_NOT_A_LEVER";
        string path = TempPath();

        Environment.SetEnvironmentVariable(lever, "on");
        Environment.SetEnvironmentVariable(other, "secret");
        try
        {
            var rec = new TelemetryRecorder();
            rec.Start(path, new TelemetrySessionInfo());
            rec.Stop();

            JsonElement env = Session(ReadLines(path)[0]).GetProperty("env");
            Assert.Equal("on", env.GetProperty(lever).GetString());
            Assert.False(env.TryGetProperty(other, out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable(lever, null);
            Environment.SetEnvironmentVariable(other, null);
        }
    }

    [Fact]
    public void Consumer_pairs_land_under_the_game_section_in_one_call()
    {
        // The F1-overlay dump: one call, whatever the game already has as key/value durables.
        var durables = new Dictionary<string, string>
        {
            ["worldSeed"] = "8812345",
            ["zone"] = "Ashfall",
            ["characterLevel"] = "17",
        };

        var info = new TelemetrySessionInfo { AppName = "Ruinborne" }.AddGameValues(durables);

        JsonElement session = Session(TelemetrySessionHeader.Build(info, NoEnvironment()));
        JsonElement game = session.GetProperty("game");
        Assert.Equal("8812345", game.GetProperty("worldSeed").GetString());
        Assert.Equal("Ashfall", game.GetProperty("zone").GetString());
        Assert.Equal("17", game.GetProperty("characterLevel").GetString());

        // Game values never leak into the engine-owned sections.
        Assert.False(session.GetProperty("app").TryGetProperty("zone", out _));
        Assert.False(session.TryGetProperty("worldSeed", out _));
    }

    [Fact]
    public void A_repeated_game_key_replaces_in_place_so_the_object_stays_wellformed()
    {
        var info = new TelemetrySessionInfo()
            .AddGameValue("zone", "Ashfall")
            .AddGameValue("phase", "night")
            .AddGameValue("zone", "Deepmarch");

        Assert.Equal(2, info.GameValues.Count);
        Assert.Equal("zone", info.GameValues[0].Key);          // original position kept
        Assert.Equal("Deepmarch", info.GameValues[0].Value);

        JsonElement game = Session(TelemetrySessionHeader.Build(info, NoEnvironment())).GetProperty("game");
        Assert.Equal("Deepmarch", game.GetProperty("zone").GetString());
        Assert.Equal("night", game.GetProperty("phase").GetString());
    }

    [Fact]
    public void A_blank_game_key_is_ignored_and_a_null_value_records_as_empty()
    {
        var info = new TelemetrySessionInfo()
            .AddGameValue("  ", "dropped")
            .AddGameValue("kept", null);

        Assert.Single(info.GameValues);
        JsonElement game = Session(TelemetrySessionHeader.Build(info, NoEnvironment())).GetProperty("game");
        Assert.Equal("", game.GetProperty("kept").GetString());
    }

    [Fact]
    public void Quotes_and_control_characters_in_header_values_are_escaped()
    {
        var info = new TelemetrySessionInfo { AppName = "My\"Game\\\n" }
            .AddGameValue("a\"b", "c\td");

        // Throws if the escaping is wrong, which is the whole assertion.
        JsonElement session = Session(TelemetrySessionHeader.Build(info, NoEnvironment()));
        Assert.Equal("My\"Game\\\n", session.GetProperty("app").GetProperty("name").GetString());
        Assert.Equal("c\td", session.GetProperty("game").GetProperty("a\"b").GetString());
    }

    [Fact]
    public void Non_ascii_header_values_survive_the_whole_write_and_read_path()
    {
        // Real adapter names carry these. Characters at or above 0x20 pass through unescaped and the file is
        // written as UTF-8, so this covers the encoding path and not only the in-memory string.
        const string adapter = "AMD Radeon™ RX 7900 XTX (日本語)";
        string path = TempPath();

        var rec = new TelemetryRecorder();
        rec.Start(path, new TelemetrySessionInfo { AdapterDescription = adapter, AppName = "Ruïnborne" }
            .AddGameValue("zone", "Aßhall ☃"));
        rec.Stop();

        JsonElement session = Session(ReadLines(path)[0]);
        Assert.Equal(adapter, session.GetProperty("gpu").GetProperty("adapter").GetString());
        Assert.Equal("Ruïnborne", session.GetProperty("app").GetProperty("name").GetString());
        Assert.Equal("Aßhall ☃", session.GetProperty("game").GetProperty("zone").GetString());
    }

    [Fact]
    public void GameValues_cannot_be_mutated_by_casting_it_back_to_a_list()
    {
        var info = new TelemetrySessionInfo().AddGameValue("zone", "Ashfall");

        Assert.IsNotType<List<TelemetryHeaderValue>>(info.GameValues);
        Assert.Throws<NotSupportedException>(
            () => ((IList<TelemetryHeaderValue>)info.GameValues).Add(new TelemetryHeaderValue("sneaked", "in")));

        // The view is live, so a later Add still shows through it.
        info.AddGameValue("phase", "night");
        Assert.Equal(2, info.GameValues.Count);
    }
}
