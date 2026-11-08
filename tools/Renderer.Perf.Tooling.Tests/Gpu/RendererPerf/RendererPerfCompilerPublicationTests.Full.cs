using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerPublicationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Full_fixture_enters_real_budgeted_collection_and_verifies_nonempty_files(bool unsupported, bool heldInvalid)
    {
        var report = new RendererPerfDriverTests.DriverRig(full: true).Run();
        var recorder = new RendererPerfNativeSizeFixture(retainHeldFailure: heldInvalid);
        report = report with
        {
            Conditioning = [.. report.Conditioning.Select(f => recorder.Replay(f, false))],
            Blocks = [.. report.Blocks.Select(b => b with
            {
                Warmup = [.. b.Warmup.Select(f => recorder.Replay(f, b.Block.Layout == RendererPerfLayout.PerPass))],
                Measured = [.. b.Measured.Select(f => recorder.Replay(f, b.Block.Layout == RendererPerfLayout.PerPass))]
            })],
            Controls = [.. report.Controls.Select(c => c with
            { Warmup = [.. c.Warmup.Select(f => recorder.Replay(f, false))], Measured = [.. c.Measured.Select(f => recorder.Replay(f, false))] })],
            Assessment = report.Assessment with { ValidForPromotion = !heldInvalid, Reasons = heldInvalid ? ["held recorder failure retained"] : [] },
            Publication = report.Publication with { FailureText = new(true, 0, 6) }
        };
        if (heldInvalid) report = report with
        {
            Publication = report.Publication with
            { State = RendererPerfPublicationState.Failed, FinalUnitPublished = false, OriginalFailure = "held recorder failure retained" }
        };
        using var f = new RendererPerfCompilerPublicationFixture(report, unsupported);
        var tooMany = new RefusedApi();
        if (heldInvalid) f.Boundary = stage => { if (stage == "read" && f.Diagnostic.CompilerReads == 2) f.Diagnostic.CompilerApi = tooMany; };
        var writer = RendererPerfPublicationWriter.Files(f.Directory);
        var prepared = writer.PrepareCollection(f.Report, f.Inventory, f.Budget);
        Assert.True(prepared.Ready, string.Join(" ", prepared.Failures.Select(e => e.Error.Message)));
        var cold = f.Collect(writer);
        Assert.Equal(heldInvalid, !cold.Failures.IsEmpty);
        var published = writer.Publish(f.Report with { Compiler = cold.Compiler, Pipelines = cold.Pipelines }, f.Inventory, f.Budget);
        Assert.Empty(published.Failures);
        var accounting = published.Report.Compiler.Accounting!;
        Assert.Equal(2, accounting.Admissions.Length);
        Assert.All(accounting.Admissions, a => Assert.NotNull(a.CommittedBytes));
        long prior = 0;
        foreach (var admission in accounting.Admissions)
        {
            long available = 268435456L - accounting.BaseRootBytes - accounting.InventoryBytes - prior - 16777216;
            int total = checked((int)Math.Min(16384, (available - 786432) / 4096));
            Assert.Equal(new GpuPipelineCompilerReadRequest(64, 256, total, 256, 16384, 4), admission.Request);
            Assert.Equal(786432 + 4096 * total, admission.MaximumEncodedBytes);
            prior += admission.CommittedBytes!.Value;
        }
        foreach (var admission in accounting.Admissions.DistinctBy(a => a.Request.MaximumTotalStatistics))
            new RendererPerfCompilerProtocolTests(_output).Conditional_read_reservations_cover_maximal_escaping(
                admission.Request.MaximumTotalStatistics, admission.MaximumEncodedBytes);
        string name = report.Manifest.UnitId + (heldInvalid ? ".partial" : "") + ".json";
        byte[] bytes = File.ReadAllBytes(Path.Combine(f.Directory, name));
        var decoded = RendererPerfReportJson.Parse(bytes);
        var before = RendererPerfCompactReportTests.Frames(report).ToArray();
        var after = RendererPerfCompactReportTests.Frames(decoded).ToArray();
        Assert.Equal(16944, before.Length); Assert.Equal(before.Length, after.Length);
        var compared = new HashSet<(object, object)>();
        for (int n = 0; n < before.Length; n++)
        {
            var a = before[n]; var b = after[n];
            Assert.Equal(a with { Metrics = b.Metrics, Passes = b.Passes, CpuPhases = b.CpuPhases, Errors = b.Errors }, b);
            Assert.Equal(a.Passes.ToArray(), b.Passes.ToArray()); Assert.Equal(a.CpuPhases.ToArray(), b.CpuPhases.ToArray());
            Assert.Equal(a.Errors.ToArray(), b.Errors.ToArray());
            if (compared.Add((a.Metrics, b.Metrics))) Assert.Equal(a.Metrics.OrderBy(p => p.Key), b.Metrics.OrderBy(p => p.Key));
        }
        long combined = checked(bytes.LongLength + decoded.Sidecars.Where(s => s.Kind != RendererPerfSidecarKind.NonCompiler).Sum(s => new FileInfo(Path.Combine(f.Directory, s.Path)).Length));
        Assert.InRange(combined, 1, 268435456);
        Assert.Equal(129, f.Inventory.Occurrences.Length); Assert.Equal(2, decoded.Pipelines.Length);
        if (heldInvalid)
        {
            Assert.False(decoded.Assessment.ValidForPromotion); Assert.False(decoded.Publication.FinalUnitPublished);
            Assert.Equal(RendererPerfCollectionState.Invalid, decoded.Compiler.State);
            Assert.Contains(decoded.Compiler.Results, r => r.State == RendererPerfCollectionState.Invalid);
            Assert.Contains(after.SelectMany(frame => frame.Errors), e => e == "held recorder failure retained");
            Assert.NotEmpty(decoded.Pipelines[0].Executables);
            Assert.Equal(0, tooMany.ForbiddenFills);
            Assert.ThrowsAny<Exception>(() => RendererPerfArtifactVerification.Verify(f.Directory, name));
        }
        else
        {
            var verified = RendererPerfArtifactVerification.Verify(f.Directory, name);
            Assert.Equal(combined, verified.CombinedBytes); Assert.True(verified.Report.Assessment.ValidForPromotion);
            Assert.Equal(unsupported ? 0 : 10, verified.Counts.Statistics);
            Assert.Equal(0, RunConsole(f.Directory, name).Exit);
            if (!unsupported)
            {
                var scalars = decoded.Pipelines[0].Executables[0].Statistics.Select(s => s.Metric.Value!).ToArray();
                Assert.True(scalars[0].Boolean); Assert.Equal(long.MinValue, scalars[1].Int64);
                Assert.Equal(long.MaxValue, scalars[2].Int64); Assert.Equal(ulong.MaxValue, scalars[3].UInt64);
                Assert.Equal(1.25, scalars[4].Float64);
            }
        }
        var paths = decoded.Sidecars.Select(s => s.Path).Concat(new[] { name, Path.ChangeExtension(name, ".md") }).ToArray();
        string? output = Environment.GetEnvironmentVariable("KE_RENDERER_PERF_TASK4_EVIDENCE");
        string variant = heldInvalid ? "held-invalid" : unsupported ? "complete-unsupported" : "complete-small";
        if (output is not null)
        {
            output = Path.Combine(output, variant); System.IO.Directory.CreateDirectory(output);
            foreach (string path in paths) File.Copy(Path.Combine(f.Directory, path), Path.Combine(output, path), true);
        }
        _output.WriteLine("FULL_PROOF " + JsonSerializer.Serialize(new
        {
            variant,
            frames = before.Length,
            historicalBase = 199883475,
            historicalReservation = 75497472,
            historicalConflict = 23722707,
            historicalThreshold = 176160768,
            accounting,
            counts = decoded.Compiler.Counts,
            reads = decoded.Compiler.Results.Select(r => r.Read),
            rootBytes = bytes.LongLength,
            rootSha256 = RendererPerfRecipe.Hash(bytes),
            combinedBytes = combined,
            files = paths.Select(path => new
            {
                path,
                bytes = new FileInfo(Path.Combine(f.Directory, path)).Length,
                sha256 = RendererPerfRecipe.Hash(File.ReadAllBytes(Path.Combine(f.Directory, path)))
            }),
            retainedDirectory = output
        }, RendererPerfManifest.JsonOptions));
    }
    sealed class RefusedApi : IVulkanPipelineExecutableApi
    {
        internal int ForbiddenFills;
        public Result Properties(ulong pipeline, ref uint count, Span<PipelineExecutablePropertiesKHR> values)
        { if (!values.IsEmpty) ForbiddenFills++; count = 65; return Result.Success; }
        public Result Statistics(ulong pipeline, uint executable, ref uint count, Span<PipelineExecutableStatisticKHR> values)
        { ForbiddenFills++; throw new InvalidOperationException("No statistics call is permitted after executable refusal."); }
    }
}
