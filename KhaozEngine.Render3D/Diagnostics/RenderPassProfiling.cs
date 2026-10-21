using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Diagnostics;

/// <summary>One scene's callback failure latch, shared by its owned renderers.</summary>
internal sealed class RenderPassProfiling(IRenderPassObserver observer)
{
    internal IRenderPassObserver Observer { get; } = observer;
    internal Exception? Failure { get; private set; }
    internal void Reset() => Failure = null;
    internal void ThrowIfFailed()
    {
        if (Failure is { } failure)
            throw new InvalidOperationException("The render pass observer failed. Profiling data is invalid.", failure);
    }
    internal RenderProfilingScope Pass(IGpuCommandList commands, string id, int occurrence = 0,
        bool executed = true, bool fusedMotion = false)
    {
        if (Failure is not null) return default;
        var key = new RenderPassKey(id, occurrence);
        try
        {
            if (!executed) { Observer.SkipPass(commands, key); return default; }
            Observer.BeginPass(commands, key);
            if (fusedMotion) Observer.FusedMotion(commands, key);
            return new(this, commands, key);
        }
        catch (Exception error) { Failure ??= error; return default; }
    }
    internal RenderProfilingScope Cpu(string id)
    {
        if (Failure is not null) return default;
        try { Observer.BeginCpuPhase(id); return new(this, null, new(id)); }
        catch (Exception error) { Failure ??= error; return default; }
    }
    internal void End(IGpuCommandList? commands, RenderPassKey key)
    {
        if (Failure is not null) return;
        try
        {
            if (commands is null) Observer.EndCpuPhase(key.Id);
            else Observer.EndPass(commands, key);
        }
        catch (Exception error) { Failure ??= error; }
    }
}

/// <summary>A value scope. Default is inert, with no allocation or GPU operation.</summary>
internal readonly struct RenderProfilingScope(RenderPassProfiling? profiling, IGpuCommandList? commands,
    RenderPassKey key) : IDisposable
{
    public void Dispose() => profiling?.End(commands, key);
}
