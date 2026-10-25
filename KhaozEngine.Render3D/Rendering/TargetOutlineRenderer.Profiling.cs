using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class TargetOutlineRenderer
{
    // Parts can disappear after a group was queued. Keep its reserved occurrences in native render order.
    internal void SkipGroup(IGpuCommandList commands, int group)
    {
        Profiling?.Pass(commands, "outline.uploads", group, executed: false);
        Profiling?.Pass(commands, "outline.mask", group, executed: false);
        Profiling?.Pass(commands, "outline.mask-resolve", group, executed: false);
        Profiling?.Pass(commands, "outline.mask-mips", group, executed: false);
        Profiling?.Pass(commands, "outline.visible-mask", group, executed: false);
        Profiling?.Pass(commands, "outline.visible-resolve", group, executed: false);
        Profiling?.Pass(commands, "outline.visible-mips", group, executed: false);
        Profiling?.Pass(commands, "outline.composite", group, executed: false);
    }
}
