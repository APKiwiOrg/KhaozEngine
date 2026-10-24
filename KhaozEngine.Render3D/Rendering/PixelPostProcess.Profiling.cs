using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class PixelPostProcess
{
    void SkipPostPass(IGpuCommandList cl, string id)
    {
        if (Profiling is null) return;
        if (id == "post.bloom")
        {
            Profiling.Pass(cl, "post.bloom.bright", executed: false);
            Profiling.Pass(cl, "post.bloom.horizontal", executed: false);
            Profiling.Pass(cl, "post.bloom.vertical", executed: false);
            Profiling.Pass(cl, "post.bloom.composite", executed: false);
        }
        else Profiling.Pass(cl, id, executed: false);
    }
}
