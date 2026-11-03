using System;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed partial class MetalGpuDevice
{
    [SupportedOSPlatform("macos")]
    sealed class EarlyFrameApi(MetalGpuDevice device) : IMetalEarlyFrameApi
    {
        public bool IsAlive => !device._liveness.IsDead;
        public void FlushSetup() => device._setup.Flush();
        public void CommitWithCompletionHandler(IntPtr buffer, MetalCommandList list)
        {
            if (!MetalCompletionHandler.AttachTo(buffer))
                throw new InvalidOperationException("Metal could not attach the prefix completion error handler.");
            new MTLCommandBuffer(buffer).Commit(FrameTimingCommandKind.EarlyFrameRecording, list);
        }
    }
}
