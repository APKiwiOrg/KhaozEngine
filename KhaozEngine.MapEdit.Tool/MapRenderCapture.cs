using System;
using KhaozEngine.Gpu;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEdit;

internal static class MapRenderCapture
{
    internal static byte[] Capture(Func<byte[]> capture)
    {
        try { return capture(); }
        catch (Exception ex)
        {
            GpuBackendKind selected = GpuBackendSelector.Select();
            MapDocumentFinding? finding = ex switch
            {
                MapDocumentException document => MapDocumentFinding.FromException(document),
                MapRenderException render => render.Finding,
                _ => null,
            };
            MapDocumentRefusal reason = ex is MapRenderException known ? known.Reason : finding?.Reason ?? MapDocumentRefusal.RenderFailed;
            throw new MapRenderException(reason, "render failed for backend " + selected
                + ". The reason is in the details below: a machine with no headless device, a backend whose "
                + "provider was never registered, and a failure inside the render itself all arrive here. Set "
                + "KE_GRAPHICS_BACKEND to render on a different backend. Details: " + ex.Message, ex)
            { Backend = selected, Operation = "render", Finding = finding };
        }
    }

    // Inject only a capture fault through the real wrapper. No device, global backend mutation or live scene escapes.
    internal static byte[] CaptureFailureForTests(Exception? fault) => Capture(() => fault is null ? [0, 0, 0, 255] : throw fault);
}
