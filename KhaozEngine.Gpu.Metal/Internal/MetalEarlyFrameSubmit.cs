using System;

namespace KhaozEngine.Gpu.Metal.Internal;

internal interface IMetalEarlyFrameSubmitApi
{
    bool IsAlive { get; }
    void FlushSetup();
    bool AttachCompletionHandler(IntPtr buffer);
    void Commit(IntPtr buffer, MetalCommandList commands);
}

/// <summary>Same setup-before-submit lock order and error handler as the final submit, without a timeline signal.</summary>
internal sealed class MetalEarlyFrameSubmit(IMetalEarlyFrameSubmitApi api, object submitLock)
{
    internal bool TryCommit(MetalCommandList list, IntPtr buffer)
    {
        if (!api.IsAlive) return false;
        api.FlushSetup();
        lock (submitLock)
        {
            if (!api.IsAlive) return false;
            if (!api.AttachCompletionHandler(buffer))
                throw new InvalidOperationException("The early frame completion handler could not be attached.");
            list.ProtectEarlyFrame();
            api.Commit(buffer, list);
            return true;
        }
    }
}
