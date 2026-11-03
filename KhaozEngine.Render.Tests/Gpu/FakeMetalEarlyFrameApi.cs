using System;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Tests.Gpu;

internal sealed class FakeMetalEarlyFrameApi : IMetalEarlyFrameApi
{
    public bool IsAlive { get; set; } = true;
    internal Action? OnCommit;
    internal Action? OnFlush;
    public void FlushSetup() => OnFlush?.Invoke();
    public void CommitWithCompletionHandler(IntPtr buffer, MetalCommandList list) => OnCommit?.Invoke();
}
