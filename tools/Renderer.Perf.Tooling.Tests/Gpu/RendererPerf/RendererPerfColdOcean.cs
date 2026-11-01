using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Complete the prepared ocean's compute work without consuming scene or history indices.</summary>
internal static class RendererPerfColdOcean
{
    internal static void Record(Scene3D scene, IGpuDevice device)
    {
        // Read existing owners only, as native admission does. Never alter the producer's clock or latches.
        var water = Field<WaterRenderer>(scene, "_water");
        var ocean = Field<OceanFftProducer>(water, "_ocean");
        using var commands = device.Factory.CreateCommandList();
        commands.Begin();
        if (!ocean.Record(commands))
            throw new RendererPerfProtocolException("Startup", "Cold ocean preparation did not produce its planned work.");
        commands.End();
        device.Submit(commands);
        device.WaitForIdle();
    }

    static T Field<T>(object owner, string name) where T : class =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner) as T
        ?? throw new RendererPerfProtocolException("Startup", "The cold ocean owner is unavailable: " + name);
}
