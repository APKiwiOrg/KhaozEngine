using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Benchmarks.Catalog;
using KhaozEngine.Benchmarks.CatalogCrash;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Benchmarks;

/// <summary>
/// Headless acceptance for the content catalog benchmark modes, <c>--catalog</c> and
/// <c>--catalog-crash-probe</c>, which spec 14.2 asks for as a <c>CatalogBenchmarkTests</c> class in this
/// assembly mirroring <c>MutationJournalBenchmarkTests</c>. What is asserted is STRUCTURAL: the options the
/// spec names parse, a malformed one refuses, the synthetic content set is byte for byte repeatable at one
/// seed, the result serializes and round trips, the output writer lands a file, and a tiny run completes and
/// fills exactly the phases it ran. No TIMING figure and no budget is asserted anywhere here, because a
/// wall-clock number on a shared CI runner is observational rather than a fact a test may hold.
/// <para>
/// Joins the AllocSensitive collection because a run measures its own heap through
/// <c>GC.GetTotalMemory(forceFullCollection: true)</c> and the text micro loop allocates heavily, neither of
/// which should race the GC-churning parallel tests.
/// </para>
/// </summary>
[Collection("AllocSensitive")]
public sealed class CatalogBenchmarkTests
{
    /// <summary>The nine points the crash probe kills a publish at, in the pipeline order it reports them.</summary>
    private static readonly ContentPublishStep[] ProbeSteps =
    [
        ContentPublishStep.BeforeIdAllocation,
        ContentPublishStep.AfterIdAllocation,
        ContentPublishStep.BeforeChunkWrite,
        ContentPublishStep.AfterChunkWrite,
        ContentPublishStep.BeforeManifestWrite,
        ContentPublishStep.AfterManifestWrite,
        ContentPublishStep.BeforeCommit,
        ContentPublishStep.AfterCommit,
        ContentPublishStep.DuringSweep,
    ];

    [Fact]
    public void Parse_accepts_the_six_documented_options_and_rejects_malformed_values()
    {
        CatalogBenchmarkConfig config = Parse(
            "--catalog", "--definitions", "2048", "--types", "8", "--chunk-slots", "512",
            "--languages", "3", "--edit-count", "2", "--compose", "--seed", "917");

        Assert.Equal(2048, config.Definitions);
        Assert.Equal(8, config.Types);
        Assert.Equal(512, config.ChunkSlots);
        Assert.Equal(3, config.Languages);
        Assert.Equal(2, config.EditCount);
        Assert.True(config.Compose);
        Assert.Equal(917, config.Seed);
        Assert.Equal(CatalogPhases.Compose, config.Phases);

        CatalogBenchmarkConfig defaults = Parse("--catalog");
        Assert.Equal(50_000, defaults.Definitions);
        Assert.Equal(CatalogBenchmarkConfig.EngineTypeCount, defaults.Types);
        Assert.Equal(1_024, defaults.ChunkSlots);
        Assert.Equal(1, defaults.Languages);
        Assert.Equal(1, defaults.EditCount);
        Assert.False(defaults.Compose);
        Assert.Equal(CatalogPhases.All, defaults.Phases);
        Assert.Null(defaults.OutputPath);

        // A value that is not an integer, an option with no value at all, an unknown option and an unknown
        // phase name are all refused before anything runs.
        Assert.Throws<ArgumentException>(() => Parse("--catalog", "--definitions", "fifty thousand"));
        Assert.Throws<ArgumentException>(() => Parse("--catalog", "--languages", "several"));
        Assert.Throws<ArgumentException>(() => Parse("--catalog", "--edit-count"));
        Assert.Throws<ArgumentException>(() => Parse("--catalog", "--phases", "publish,invented"));
        Assert.Throws<ArgumentException>(() => Parse("--catalog", "--definitons", "2048"));

        // A value that parses but falls outside the bound the config declares.
        Assert.Throws<ArgumentOutOfRangeException>(() => Parse("--catalog", "--definitions", "511"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Parse("--catalog", "--types", "5"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Parse("--catalog", "--chunk-slots", "1000"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Parse("--catalog", "--languages", "17"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Parse("--catalog", "--edit-count", "0"));
    }

    [Fact]
    public void Synthetic_content_is_byte_for_byte_repeatable_at_one_seed()
    {
        CatalogBenchmarkConfig config = Parse("--catalog", "--definitions", "1024", "--types", "8", "--seed", "835");

        (byte[] first, long firstKeyBytes) = EncodeEveryRow(config);
        (byte[] second, long secondKeyBytes) = EncodeEveryRow(config);
        (byte[] otherSeed, _) = EncodeEveryRow(config with { Seed = 836 });

        Assert.True(first.Length > 0);
        Assert.True(first.AsSpan().SequenceEqual(second), "One seed generated two different row sets.");
        Assert.Equal(firstKeyBytes, secondKeyBytes);
        Assert.False(first.AsSpan().SequenceEqual(otherSeed), "Two seeds generated the same row set.");
    }

    [Fact]
    public void Result_json_is_stable_camel_cased_and_round_trips()
    {
        var result = new CatalogBenchmarkResult
        {
            Machine = "test-machine",
            OperatingSystem = "test-os",
            Framework = ".NET test",
            ProcessorCount = 4,
            Phases = nameof(CatalogPhases.Compose),
            Seed = 835,
            Definitions = 512,
            Types = 6,
            ChunkSlots = 1_024,
            Languages = 1,
            EditCount = 1,
            LinkBitsPerSecond = 20_000_000,
            FetchConcurrency = 4,
            ServerPackStoredBytes = 49_302,
            ClientPackStoredBytes = 46_142,
            ComposeBootMs = 89.25,
            ComposePublishedThisRun = true,
            ComposeAttribution = new Dictionary<string, double> { ["p3LoadMs"] = 31.5 },
        };

        string json = result.ToJson();

        Assert.Equal(json, result.ToJson());
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal("catalog", root.GetProperty("mode").GetString());
        Assert.Equal("test-machine", root.GetProperty("machine").GetString());
        Assert.Equal(49_302, root.GetProperty("serverPackStoredBytes").GetInt64());
        Assert.Equal(89.25, root.GetProperty("composeBootMs").GetDouble(), 6);
        Assert.Equal(31.5, root.GetProperty("composeAttribution").GetProperty("p3LoadMs").GetDouble(), 6);

        // A phase that did not run reports null, never a zero it did not measure.
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lookupNanoseconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("coldStartMs").ValueKind);

        CatalogBenchmarkResult? restored = JsonSerializer.Deserialize<CatalogBenchmarkResult>(
            json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.NotNull(restored);
        Assert.Equal(json, restored.ToJson());
    }

    [Fact]
    public async Task Output_writer_creates_the_json_and_leaves_no_temporary_behind()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"catalog-benchmark-output-{Guid.NewGuid():N}");
        string output = Path.Combine(directory, "catalog-v1.json");
        try
        {
            var first = new CatalogBenchmarkResult { Seed = 835, Definitions = 512 };
            CatalogBenchmarkResult second = first with { Definitions = 1_024 };

            await CatalogBenchmarkOutput.WriteAsync(first, output);
            Assert.Equal(first.ToJson() + Environment.NewLine, await File.ReadAllTextAsync(output, Encoding.UTF8));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
            Assert.Equal((byte)'{', (await File.ReadAllBytesAsync(output))[0]);

            await CatalogBenchmarkOutput.WriteAsync(second, output);
            Assert.Equal(second.ToJson() + Environment.NewLine, await File.ReadAllTextAsync(output, Encoding.UTF8));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));

            await Assert.ThrowsAsync<ArgumentException>(
                () => CatalogBenchmarkOutput.WriteAsync(first, "catalog-v1.json"));
            await Assert.ThrowsAsync<ArgumentException>(
                () => CatalogBenchmarkOutput.WriteAsync(first, Path.Combine(directory, "catalog-v1.txt")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_tiny_run_completes_and_fills_only_the_phases_it_ran()
    {
        CatalogBenchmarkConfig config = Parse(
            "--catalog", "--quick", "--definitions", "512", "--chunk-slots", "256", "--seed", "835",
            "--phases", "publish,load,edit,text");

        CatalogBenchmarkResult result = await CatalogBenchmarkRunner.RunAsync(
            config, TextWriter.Null, CancellationToken.None);

        Assert.Equal(512, result.Definitions);
        Assert.Equal(835, result.Seed);
        Assert.Equal(256, result.ChunkSlots);

        // The publish happened, and the client pack is smaller by exactly the ServerOnly families.
        Assert.True(result.ServerPackStoredBytes > 0);
        Assert.True(result.ClientPackStoredBytes < result.ServerPackStoredBytes);
        Assert.True(result.ChunkCount > 0);
        Assert.True(result.ItemChunkCount > 0);
        Assert.True(result.TotalRows > 0);

        // The runtime loaded, validated clean, and the two micro loops allocated nothing.
        Assert.NotNull(result.LoadTotalMs);
        Assert.NotNull(result.RuntimeHeapBytes);
        Assert.NotNull(result.RuntimeApproximateBytes);
        Assert.Equal(0, result.ValidatorFindings);
        Assert.True(result.ValidatorRowsSwept > 0);
        Assert.NotNull(result.LookupNanoseconds);
        Assert.Equal<long?>(0, result.LookupAllocatedBytes);
        Assert.NotNull(result.LootDrawNanoseconds);
        Assert.Equal<long?>(0, result.LootDrawAllocatedBytes);
        Assert.Equal(200, result.LootDrawTableEntries);

        // One edited item rewrote exactly one chunk, and one language decoded.
        Assert.Equal(1, result.EditAffectedChunks);
        Assert.True(result.EditChunkBytes > 0);
        Assert.True(result.TextDecodeEntryCount > 0);

        // The two phases that were not asked for report nothing at all.
        Assert.Null(result.ColdStartMs);
        Assert.Null(result.ComposeBootMs);
        Assert.Null(result.ComposeAttribution);

        using JsonDocument document = JsonDocument.Parse(result.ToJson());
        Assert.Equal(512, document.RootElement.GetProperty("definitions").GetInt32());
    }

    [Fact]
    public async Task A_tiny_compose_run_reports_the_boot_and_its_attribution()
    {
        CatalogBenchmarkConfig config = Parse("--catalog", "--quick", "--definitions", "512", "--compose");
        Assert.Equal(CatalogPhases.Compose, config.Phases);

        CatalogBenchmarkResult result = await CatalogBenchmarkRunner.RunAsync(
            config, TextWriter.Null, CancellationToken.None);

        Assert.Equal(nameof(CatalogPhases.Compose), result.Phases);
        Assert.NotNull(result.ComposeBootMs);
        Assert.NotNull(result.ComposeHeapBytes);
        Assert.NotNull(result.ComposeP3Ms);
        Assert.NotNull(result.ComposeP10Ms);
        Assert.Equal<long?>(CatalogBenchmarkRunner.StandInLoadIndexBytes, result.ComposeLoadIndexBytes);

        // A run that published first has that publish inside its elapsed time, so the process figure is null
        // rather than a boot it cannot honestly claim.
        Assert.True(result.ComposePublishedThisRun);
        Assert.Null(result.ComposeProcessStartMs);

        Assert.NotNull(result.ComposeAttribution);
        Assert.Equal(3, result.ComposeAttribution.Count);
        Assert.Contains("p3LoadMs", result.ComposeAttribution);
        Assert.Contains("p10TextMs", result.ComposeAttribution);
        Assert.Contains("standInLoadIndexMs", result.ComposeAttribution);

        // Compose was the only phase, so P3's own group and the micro loops stay null.
        Assert.Null(result.LoadTotalMs);
        Assert.Null(result.LookupNanoseconds);
        Assert.Null(result.EditPublishMs);
    }

    [Fact]
    public async Task Crash_probe_parse_rejects_a_malformed_argument_before_it_touches_a_store()
    {
        string root = Path.Combine(Path.GetTempPath(), "catalog-crash-probe-parse");

        await Assert.ThrowsAsync<ArgumentException>(() => Probe("--catalog-crash-probe", "--invented"));
        await Assert.ThrowsAsync<ArgumentException>(() => Probe("--catalog-crash-probe", "--catalog-crash-probe"));
        await Assert.ThrowsAsync<ArgumentException>(() => Probe("--catalog-crash-probe", "--root"));
        await Assert.ThrowsAsync<ArgumentException>(() => Probe("--catalog-crash-probe", "--root", "relative/root"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => Probe("--catalog-crash-probe", "--child", "--root", root, "--pause-at", "Whenever"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => Probe("--catalog-crash-probe", "--child", "--root", root, "--pause-at", "beforecommit"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => Probe("--catalog-crash-probe", "--child", "--pause-at", "BeforeCommit"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => Probe("--catalog-crash-probe", "--child", "--root", root));
        await Assert.ThrowsAsync<ArgumentException>(() => Probe("--child", "--root", root));

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task The_crash_probe_run_recovers_every_step_and_exits_zero()
    {
        ProbeRun run = await RunCrashProbeAsync();

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("FAILED", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_crash_probe_output_names_the_control_publish_and_every_step_in_order()
    {
        ProbeRun run = await RunCrashProbeAsync();

        string[] lines = ReportLines(run.Output);

        Assert.Equal(ProbeSteps.Length + 2, lines.Length);
        Assert.Contains("control publish is version 2", lines[0], StringComparison.Ordinal);
        for (int i = 0; i < ProbeSteps.Length; i++)
        {
            Assert.Contains(ProbeSteps[i].ToString(), lines[i + 1], StringComparison.Ordinal);
            Assert.Contains("every referenced file serves", lines[i + 1], StringComparison.Ordinal);
        }

        Assert.EndsWith(
            "9/9 steps left the store whole and republished to the same manifest",
            lines[^1],
            StringComparison.Ordinal);
    }

    private static CatalogBenchmarkConfig Parse(params string[] args) => CatalogBenchmarkConfig.Parse(args);

    private static Task Probe(params string[] args) => CatalogCrashProbe.RunAsync(args);

    /// <summary>Every row of every registered type, encoded end to end, which is what a seed has to repeat.</summary>
    private static (byte[] Bytes, long KeyBytes) EncodeEveryRow(CatalogBenchmarkConfig config)
    {
        var content = new SyntheticContentSet(config);
        var encoder = new SyntheticRowEncoder(content);
        using var bytes = new MemoryStream();
        byte[] row = new byte[8192];
        foreach (ContentTypeDescriptor type in content.Types)
        {
            foreach (int id in content.IdsFor(type.TypeId))
            {
                int written = encoder.Encode(type.TypeId, id, row);
                bytes.Write(row, 0, written);
            }
        }

        return (bytes.ToArray(), encoder.KeyBytes);
    }

    /// <summary>
    /// The probe runs OUT OF PROCESS here, the way the journal's kill test spawns its own. In process it
    /// would write the harness's report to the shared console and set <c>Environment.ExitCode</c>, which is
    /// process-global state a test has no business moving.
    /// </summary>
    private static async Task<ProbeRun> RunCrashProbeAsync()
    {
        string benchmarks = typeof(CatalogCrashProbe).Assembly.Location;
        string runtimeConfig = Path.ChangeExtension(benchmarks, ".runtimeconfig.json");
        Assert.True(File.Exists(runtimeConfig), $"Benchmark runtime config is missing beside {benchmarks}.");

        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(benchmarks);
        start.ArgumentList.Add("--catalog-crash-probe");

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("The catalog crash probe did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new ProbeRun(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    /// <summary>
    /// The probe's own report lines only. Anything the host prints around them (a first-run SDK notice, a
    /// runtime warning) is not the probe talking and would make a line count a lie.
    /// </summary>
    private static string[] ReportLines(string output)
    {
        var lines = new List<string>();
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.StartsWith("catalog-crash-probe: ", StringComparison.Ordinal)) lines.Add(trimmed);
        }

        return lines.ToArray();
    }

    private readonly record struct ProbeRun(int ExitCode, string Output, string Error);
}
