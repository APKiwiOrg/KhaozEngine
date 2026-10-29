using System;
using KhaozEngine.Tests.Gpu.RendererPerf;
using KhaozEngine.Gpu;

bool profiles = args.Length == 3 && args[0] == "publish-profiles" && args[1] == "--request";
bool publish = profiles || args.Length == 3 && args[0] == "publish" && args[1] == "--request";
bool admit = args.Length == 3 && args[0] == "admit";
if (!publish && !admit && args.Length != 2)
{
    Console.Error.WriteLine("Expected evidence directory and report filename, publish/publish-profiles --request and request filename, or admit and checkout and authorization directories.");
    return 2;
}
try
{
    RequireNoProviders();
    if (admit) RendererPerfManifestAdmission.Admit(args[1], args[2]);
    else if (publish)
    {
        _ = profiles ? RendererPerfProfilePublisher.PublishFile(args[2]) : RendererPerfBaselinePublisher.PublishFile(args[2]);
    }
    else _ = RendererPerfArtifactVerification.Verify(args[0], args[1]);
    RequireNoProviders();
    Console.WriteLine(admit ? "Admitted renderer manifest with no GPU providers registered."
        : profiles ? "Published immutable profile files with no GPU providers registered."
        : publish ? "Published immutable baseline files with no GPU providers registered."
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
