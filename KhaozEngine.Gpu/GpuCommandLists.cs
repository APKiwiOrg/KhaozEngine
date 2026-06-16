using Veldrid;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// TRANSITIONAL bridge for presenting a Veldrid <see cref="CommandList"/> as an <see cref="IGpuCommandList"/>.
    /// Needed because the windowing frame loop still records into a raw Veldrid command list until phase 3c, but
    /// the migrated Render2D draws through the engine command-list interface. The returned wrapper is NON-owning:
    /// disposing it does NOT dispose the borrowed command list (the window owns its lifetime). Goes away after 3c.
    /// </summary>
    public static class GpuCommandLists
    {
        /// <summary>Wrap an existing Veldrid command list as a non-owning <see cref="IGpuCommandList"/>.</summary>
        public static IGpuCommandList Wrap(CommandList cl) => new VeldridGpuCommandList(cl, owns: false);
    }
}
