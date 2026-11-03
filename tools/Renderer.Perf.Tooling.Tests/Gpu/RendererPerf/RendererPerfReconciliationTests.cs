using System;
using System.Linq;
using System.Text;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfReconciliationTests
{
    [Theory]
    [InlineData("effects-ui")]
    [InlineData("split")]
    public void Visibility_clear_retains_its_leaf_and_native_order(string mode)
    {
        using var rig = Warm(mode);
        var observer = new PostTraceObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame(68);
        var clear = Assert.Single(rig.Commands.Events, e => e.Op == "clear-color" && e.X == 4);
        Assert.Equal("opaque.visibility-clear", clear.Owner?.Id);
        int index = rig.Commands.Events.IndexOf(clear);
        Assert.Equal("motion.clear", rig.Commands.Events[index - 1].Owner?.Id);
        Assert.Equal("opaque.depth-clear", rig.Commands.Events[index + 1].Owner?.Id);
    }

    [Theory]
    [InlineData("effects-ui")]
    [InlineData("split")]
    public void Visibility_history_copy_has_one_leaf_after_resolve(string mode)
    {
        using var rig = Warm(mode);
        var observer = new PostTraceObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame(68);
        var copy = Assert.Single(rig.Commands.Events, e => e.Op == "copy-texture"
            && rig.Commands.Resource(e.A) is IGpuTexture { Format: GpuPixelFormat.R8UNorm });
        Assert.Equal("temporal.visibility-store", copy.Owner?.Id);
        var definitions = observer.Freeze();
        Assert.Equal(-1, Assert.Single(definitions, p => p.Id == "temporal.visibility-store").Parent);
        Assert.True(rig.Commands.Events.FindIndex(e => e.Owner?.Id == (mode == "split"
            ? "temporal.accumulate" : "temporal.depth-store")) < rig.Commands.Events.IndexOf(copy));
    }

    [Theory]
    [InlineData("effects-ui")]
    [InlineData("outdoors")]
    [InlineData("split")]
    public void Actual_recorded_shader_sources_are_present_in_executable_identity(string mode)
    {
        using var rig = Warm(mode);
        var hashes = RendererPerfExecutable.ShaderHashes.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var request in rig.Commands.Events.Select(e => e.Pipeline).OfType<FakePipeline>()
            .Where(p => p.Request is not null).Select(p => p.Request!.Value).Distinct())
        {
            Assert.Contains(RendererPerfRecipe.Hash(Encoding.UTF8.GetBytes(request.VertexGlsl)), hashes);
            string hash = RendererPerfRecipe.Hash(Encoding.UTF8.GetBytes(request.FragmentGlsl));
            Assert.True(hashes.Contains(hash), "Missing recorded fragment " + hash + "\n" + request.FragmentGlsl);
        }
    }

    static PostTraceRig Warm(string mode)
    {
        var rig = new PostTraceRig(mode);
        rig.Frame(64); rig.Frame(65); rig.Frame(66); rig.Frame(67);
        return rig;
    }
}
