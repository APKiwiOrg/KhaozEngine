using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed partial class MetalGpuDevice : IMetalEarlyFrameSubmitApi
{
    bool IMetalEarlyFrameSubmitApi.IsAlive => !_disposed && !_liveness.IsDead && KhaozEngineMetal.IsPlatformSupported;
    void IMetalEarlyFrameSubmitApi.FlushSetup() => _setup.Flush();
    bool IMetalEarlyFrameSubmitApi.AttachCompletionHandler(IntPtr buffer)
    {
        if (!KhaozEngineMetal.IsPlatformSupported) return false;
        return MetalCompletionHandler.AttachTo(buffer);
    }
    void IMetalEarlyFrameSubmitApi.Commit(IntPtr buffer, MetalCommandList commands)
    {
        if (!KhaozEngineMetal.IsPlatformSupported) throw new PlatformNotSupportedException();
        new MTLCommandBuffer(buffer).Commit(FrameTimingCommandKind.EarlyFrameRecording, commands);
    }
}
