using System;
using KhaozEngine.Tests.Gpu.RendererPerf;
using KhaozEngine.Gpu;

if (args.Length != 2)
{
    Console.Error.WriteLine("Expected exactly two arguments, evidence directory and report filename.");
    return 2;
}
try
{
    RequireNoProviders();
    _ = RendererPerfArtifactVerification.Verify(args[0], args[1]);
    RequireNoProviders();
    Console.WriteLine("Verified renderer artifacts with no GPU providers registered.");
    return 0;
}
catch (Exception error)
{
    string cause = error.GetType().Name + ": " + error.Message;
    Console.Error.WriteLine("Renderer evidence refused. " + cause[..Math.Min(cause.Length, 512)]);
    return 1;
}

static void RequireNoProviders()
{
    foreach (var backend in new[] { GpuBackendKind.VulkanNative, GpuBackendKind.Direct3D11Native, GpuBackendKind.MetalNative })
        if (GpuBackendProviders.TryGet(backend, out _)) throw new InvalidOperationException("Offline verification acquired a GPU provider.");
}
