using System;
using KhaozEngine.Tests.Gpu.RendererPerf;
using KhaozEngine.Gpu;

if (args.Length != 2)
{
    Console.Error.WriteLine("Expected evidence directory and report filename, or publish and request filename.");
    return 2;
}
try
{
    RequireNoProviders();
    if (args[0] == "publish")
    {
        _ = RendererPerfBaselinePublisher.PublishFile(args[1]);
    }
    else _ = RendererPerfArtifactVerification.Verify(args[0], args[1]);
    RequireNoProviders();
    Console.WriteLine(args[0] == "publish" ? "Published immutable baseline files with no GPU providers registered."
        : "Verified renderer artifacts with no GPU providers registered.");
    return 0;
}
catch (Exception error)
{
    string cause = (error is RendererPerfBaselineException baseline ? baseline.Code : error.GetType().Name) + ": " + error.Message;
    if (error is RendererPerfBaselineException && error.InnerException is { } inner)
        cause += ". " + inner.GetType().Name + ": " + inner.Message;
    Console.Error.WriteLine("Renderer evidence refused. " + cause[..Math.Min(cause.Length, 512)]);
    return 1;
}

static void RequireNoProviders()
{
    foreach (var backend in new[] { GpuBackendKind.VulkanNative, GpuBackendKind.Direct3D11Native, GpuBackendKind.MetalNative })
        if (GpuBackendProviders.TryGet(backend, out _)) throw new InvalidOperationException("Offline verification acquired a GPU provider.");
}
