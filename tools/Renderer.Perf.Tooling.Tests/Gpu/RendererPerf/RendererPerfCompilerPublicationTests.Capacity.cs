using System;
using System.IO;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerPublicationTests
{
    [Fact]
    public void Preparation_capacity_refusal_precedes_compiler_file_write()
    {
        var manifest = RendererPerfConstructionTests.Manifest();
        var capture = new RendererPerfConstructionCapture(manifest);
        using var native = new RendererPerfConstructionFixture();
        for (int op = 0; op < 9; op++)
        {
            capture.BeginOperation(op);
            if (op == 0)
            {
                var device = RendererPerfDevice.Wrap(native, new RendererPerfRecorder(1, RendererPerfLayout.TotalOnly, [], () => 0, 1000), capture);
                // Distinct sources fill the logical cap even when capture interns repeated text.
                for (int n = 0; n < 31; n++) device.Factory.CreateComputeShaderFromSpirv(new string('\u0001', 1048574)
                    + n.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).Dispose();
                device.Factory.CreateComputeShaderFromSpirv(new string('\u0001', 1048448)).Dispose();
            }
            capture.EndOperation();
        }
        var inventory = capture.Freeze(); Assert.True(inventory.Complete, inventory.Reason);
        Assert.Equal(32, native.ShaderAttempts);
        var report = RendererPerfAnalysisFixture.Report();
        report = report with
        {
            Pipelines = [],
            Recipes = [report.Recipes[0] with { SourceProvenance = new string('p', 50 * 1024 * 1024) }],
            Compiler = new(RendererPerfCollectionState.Incomplete, "Not attempted.", inventory.Counts,
                RendererPerfConstructionFixture.Production, null, [], null, [], [])
        };
        string directory = Path.Combine(Path.GetTempPath(), "renderer-precollection-cap-" + Guid.NewGuid().ToString("N"));
        try
        {
            var budget = new RendererPerfUnitBudget(new(() => 100, 1000), 100, 1500100, default);
            var result = RendererPerfPublicationWriter.Files(directory).PrepareCollection(report, inventory, budget);
            Assert.False(result.Ready); Assert.Single(result.Failures);
            Assert.Contains("capacity", result.Failures[0].Error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(directory, report.Manifest.UnitId + "-compiler-inventory.json")));
            Assert.Equal(32, inventory.Shaders.Length);
            _output.WriteLine("unique source logical bytes=33554432 roots include a 52428800-byte retained provenance field. Admission refused before compiler file write.");
        }
        finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
    }
}
