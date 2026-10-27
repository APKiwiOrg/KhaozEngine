using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfConstructionTests
{
    [Fact]
    public void Full_schedule_repeated_construction_fits_without_losing_occurrences_or_exact_sources()
    {
        var elapsed = Stopwatch.StartNew();
        // Source lengths and sharing from the first complete S2 operation in rehearsal run 40414700520.
        // All 43 operations use this largest observed scene, including the smaller controls.
        int[] lengths = [
            189, 10021, 12589, 11882, 1559, 564, 2159, 2341, 2148, 1960, 2483, 2472,
            1494, 2060, 1411, 1188, 3340, 77953, 73408, 5522, 71581, 72613, 3661, 77830,
            2696, 79279, 963, 3758, 885, 2164, 904, 930, 511, 2369, 1380, 260,
            120, 412, 21903, 236, 499, 19693, 796, 20702, 496, 19879, 2242, 45782,
            4037, 31319, 2210, 4902, 212, 15490, 537, 26085, 74883, 1001, 418, 760,
            1311, 824, 1238, 2592, 1203, 446, 1750, 2801, 26722, 6703, 6583, 4231,
            78306, 3938, 73761, 2285, 78181, 3167, 79630, 6637, 72010, 72966, 19885, 20894,
            20071, 610, 1503, 28445, 96983, 73224, 2276, 6585, 5201, 414, 97013, 73254
        ];
        int[][] stages = [
            [0, 1], [0, 2], [0, 3], [4, 5], [6, 7], [6, 8], [9, 10], [9, 11],
            [12, 5], [13, 14], [13, 15], [16, 17], [16, 18], [19, 20], [19, 21], [22, 23],
            [24, 25], [0, 26], [0, 27], [0, 28], [0, 29], [0, 30], [0, 31], [0, 32],
            [0, 33], [0, 34], [35, 36], [35, 36], [37, 38], [0, 39], [0, 40], [37, 41],
            [42, 43], [44, 45], [46, 47], [48, 49], [50, 51], [52, 53], [52, 54], [55, 56],
            [35, 36], [57, 58], [59, 60], [61, 62], [61, 63], [64, 65], [64, 66], [0, 67],
            [68, 56], [69], [70], [71, 72], [73, 74], [73, 72], [75, 76], [77, 78],
            [79, 80], [79, 81], [37, 82], [42, 83], [44, 84], [57, 85], [59, 86], [0, 87],
            [0, 88], [0, 89], [0, 90], [91, 72], [92, 5], [92, 93], [0, 94], [0, 95]
        ];
        string[] sources = lengths.Select((length, id) =>
        {
            string prefix = "#version 450\nlayout(local_size_x = 128) in; // source " + id + "\n";
            return prefix + new string('x', length - prefix.Length);
        }).ToArray();
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(Manifest("full"));
        for (int op = 0; op < 43; op++)
        {
            capture.BeginOperation(op);
            var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
            var shaders = new IGpuShaderSet?[72]; var compute = new IGpuComputeShader?[72];
            var layouts = new IGpuResourceLayout[62];
            for (int n = 0; n < 62; n++)
                layouts[n] = device.Factory.CreateResourceLayout(new([new("Binding" + n,
                    GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)]));
            for (int n = 0; n < 72; n++)
            {
                var ids = stages[n];
                // Distinct managed strings still identify the same exact source bytes.
                string first = new(sources[ids[0]].AsSpan());
                if (ids.Length == 1) compute[n] = device.Factory.CreateComputeShaderFromSpirv(first);
                else shaders[n] = device.Factory.CreateShadersFromSpirv(first, new(sources[ids[1]].AsSpan()));
            }
            int[] graphics = Enumerable.Range(0, 72).Where(n => stages[n].Length == 2).ToArray();
            for (int n = 0; n < 92; n++)
            {
                using var pipeline = device.Factory.CreateGraphicsPipeline(new()
                {
                    ShaderSet = shaders[graphics[n % graphics.Length]]!,
                    ResourceLayouts = [layouts[n % 62]],
                    VertexLayouts = [],
                    Outputs = new(null),
                    BlendAttachments = [],
                    Topology = op % 2 == 1 && n < 26 ? GpuPrimitiveTopology.LineList : GpuPrimitiveTopology.TriangleList
                });
                using var commands = device.Factory.CreateCommandList(); commands.SetPipeline(pipeline);
            }
            foreach (var shader in compute.Where(s => s is not null))
            {
                using var pipeline = device.Factory.CreateComputePipeline(new(shader!));
                using var commands = device.Factory.CreateCommandList(); commands.SetComputePipeline(pipeline);
            }
            foreach (var shader in shaders) shader?.Dispose();
            foreach (var shader in compute) shader?.Dispose();
            foreach (var layout in layouts) layout.Dispose();
            capture.EndOperation();
        }
        var inventory = capture.Freeze();
        Assert.True(inventory.Complete, inventory.Reason);
        Assert.Equal(9804, inventory.FactoryEvents.Length);
        Assert.Equal(4042, inventory.Occurrences.Length);
        Assert.Equal(4042, inventory.Counts.BoundOccurrences);
        Assert.Equal(120, inventory.Recipes.Length);
        Assert.Equal(70, inventory.Shaders.Length);
        Assert.Equal(62, inventory.Layouts.Length);
        Assert.Equal(0, inventory.Counts.OmittedFactoryEvents);
        byte[] encoded = RendererPerfCompilerJson.Serialize(inventory, RendererPerfReportJson.MaximumBytes);
        var restored = RendererPerfCompilerJson.Parse(encoded, RendererPerfReportJson.MaximumBytes);
        Assert.Equal(inventory.FactoryEvents.ToArray(), restored.FactoryEvents.ToArray());
        Assert.Equal(inventory.Occurrences.Length, restored.Occurrences.Length);
        Assert.Equal(sources.Order(StringComparer.Ordinal), restored.Shaders.SelectMany(s => s.Stages)
            .Select(s => s.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        using var json = JsonDocument.Parse(encoded);
        Assert.Equal(96, json.RootElement.GetProperty("sources").GetArrayLength());
        Assert.True(encoded.Length < 33554432);
        int completion = RendererPerfPublicationAccount.CompletionBytes(RendererPerfProtocolTests.CompleteReport(), inventory);
        Assert.True(completion < RendererPerfPublicationAccount.CompletionReserve);
        // The evidence audit budgets 100 MiB for full raw frames and 1 MiB for the other scene/control
        // descriptors. Even 127 fixed-size D3D11 read reservations fit beside this larger capture.
        long combined = 104857600L + 1048576 + encoded.Length + RendererPerfPublicationAccount.CompletionReserve + 127L * 1048576;
        Assert.True(combined <= RendererPerfReportJson.MaximumBytes);
        _output.WriteLine("full schedule operations=43 factory=9804 pipelines=4042 recipes=120 sourceBytes=1807168 inventoryBytes={0} completionBytes={1} durationMs={2}",
            encoded.Length, completion, elapsed.ElapsedMilliseconds);
    }
}
