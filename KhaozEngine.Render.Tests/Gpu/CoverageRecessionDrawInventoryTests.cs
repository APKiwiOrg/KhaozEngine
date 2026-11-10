using System.Linq;
using System.Text.Json;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageRecessionDrawInventoryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Body_size_scene_reports_indexed_baseline_and_pulled_corners_per_pass(bool covered, bool disablePrecise)
    {
        const int actors = 32;
        var fixture = new CoverageRecessionBodySizeFixture(33, 24, 2, 103, true);
        using var inert = new CoveragePublicationDevice();
        using var device = new CoverageRecessionObservedDevice(inert);
        using var scene = new CoverageRecessionNativeScene(device, fixture, actors, covered, disablePrecise);
        using var inventory = new CoverageRecessionDrawInventory(device);
        scene.Scene.PassObserver = inventory;
        for (int frame = 0; frame < 3; frame++)
        {
            scene.Prepare(frame);
            inventory.Begin(); scene.Render(inventory); inventory.End(); scene.Submission.Submit(inventory);
        }
        scene.AssertPublished();
        string mainPass = covered ? "opaque.coverage" : "opaque.gpu-skinned";
        var main = Assert.Single(inventory.Passes[new RenderPassKey(mainPass)].Draws);
        Assert.Equal(new(!covered, covered, covered && !disablePrecise), main.Key);
        Assert.Equal(actors, main.Value.DrawCalls);
        Assert.Equal((ulong)actors * (ulong)(covered ? fixture.Topology.TriangleCount * 3 : fixture.Mesh.Indices.Length), main.Value.SubmittedElements);
        CheckFamily(covered ? "shadow.cascade.coverage" : "shadow.cascade.gpu-skinned",
            scene.Scene.Post.Quality.Shadows.ResolvedCascadeCount, actors);
        CheckFamily("shadow.point.dynamic.gpu-skinned", 6, actors);
        CheckFamily("outline.mask", actors, 1);
        CheckFamily("outline.visible-mask", actors, 1);
        void CheckFamily(string id, int occurrences, int drawsPerOccurrence)
        {
            var passes = inventory.Passes.Where(p => p.Key.Id == id && p.Value.Draws.Count > 0).ToArray();
            Assert.Equal(occurrences, passes.Length);
            Assert.Equal(occurrences, passes.Select(p => p.Key.Occurrence).Distinct().Count());
            foreach (var pass in passes)
            {
                var count = Assert.Single(pass.Value.Draws);
                Assert.Equal(new(!covered, covered, covered && !disablePrecise), count.Key);
                Assert.Equal(drawsPerOccurrence, count.Value.DrawCalls);
                Assert.Equal((ulong)drawsPerOccurrence * (ulong)(covered ? fixture.Topology.TriangleCount * 3 : fixture.Mesh.Indices.Length), count.Value.SubmittedElements);
            }
        }
        var hostPasses = inventory.Passes.Where(p => p.Value.Draws.Keys.Any(d => d.Coverage)).ToArray();
        if (covered)
        {
            Assert.Contains(hostPasses, p => p.Key.Id == "shadow.cascade.coverage");
            Assert.Contains(hostPasses, p => p.Key.Id == "shadow.point.dynamic.gpu-skinned");
            Assert.Contains(hostPasses, p => p.Key.Id == "outline.mask");
            Assert.Contains(hostPasses, p => p.Key.Id == "outline.visible-mask");
            Assert.DoesNotContain(hostPasses, p => p.Key.Id == "unscoped");
            Assert.All(hostPasses.SelectMany(p => p.Value.Draws).Where(d => d.Key.Coverage), d =>
            {
                Assert.False(d.Key.Indexed);
                Assert.Equal(!disablePrecise, d.Key.VertexPreciseMath);
                Assert.True(d.Value.SubmittedElements > 0);
            });
        }
        else Assert.Empty(hostPasses);
        output.WriteLine("COVERAGE_DRAW_INVENTORY_JSON=" + JsonSerializer.Serialize(new
        {
            schema = "khaozengine.coverage-draw-inventory/1",
            actors,
            covered,
            disableVertexPreciseMath = disablePrecise,
            fixture = fixture.Extents,
            boundary = "Innermost pass only. SubmittedElements counts vertex references for indexed draws and corners for pulled draws, multiplied by instances. It does not count distinct vertex shader invocations. Binding changes compare logical requested handles and offsets, not native encoder invalidations.",
            passes = inventory.Passes.Select(p => new
            {
                pass = p.Key,
                p.Value.PipelineSets,
                p.Value.PipelineChanges,
                p.Value.ResourceSetBinds,
                p.Value.ResourceSetChanges,
                p.Value.VertexBufferBinds,
                p.Value.IndexBufferBinds,
                draws = p.Value.Draws.Select(d => new { d.Key.Indexed, d.Key.Coverage, d.Key.VertexPreciseMath, d.Value.DrawCalls, d.Value.SubmittedElements })
            })
        }));
    }
}
