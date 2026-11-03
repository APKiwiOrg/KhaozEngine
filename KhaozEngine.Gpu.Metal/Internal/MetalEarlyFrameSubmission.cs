using System;

namespace KhaozEngine.Gpu.Metal.Internal;

internal interface IMetalEarlyFrameApi
{
    bool IsAlive { get; }
    void FlushSetup();
    void CommitWithCompletionHandler(IntPtr buffer, MetalCommandList list);
}

/// <summary>The ordinary setup-before-submit lock order, with error reporting but no frame timeline signal.</summary>
internal sealed class MetalEarlyFrameSubmission(IMetalEarlyFrameApi api, object submitLock)
{
    internal bool TryCommit(MetalCommandList list, IntPtr buffer)
    {
        if (!api.IsAlive) return false;
        api.FlushSetup();
        lock (submitLock)
        {
            if (!api.IsAlive) return false;
            list.ProtectPrefix();
            api.CommitWithCompletionHandler(buffer, list);
            list.PrefixCommitted();
            MetalFrameTimingProbe.PrefixCommitted(list);
            return true;
        }
    }
}
