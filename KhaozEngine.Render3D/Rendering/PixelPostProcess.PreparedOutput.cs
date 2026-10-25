using System;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class PixelPostProcess
{
    FinalUbo _preparedFinal;

    internal readonly record struct PreparedOutput(PixelPostProcess Owner, IPostChainTargets Targets,
        int Generation, IGpuResourceSet Blit, FinalUbo Uniforms, Color Clear);

    internal void DrawOutput(IGpuCommandList commands, IGpuFramebuffer target,
        in PreparedOutput output, bool refreshUniform)
    {
        if (!ReferenceEquals(output.Owner, this) || !ReferenceEquals(output.Targets, _bound)
            || output.Generation != _boundGen || output.Targets.Generation != output.Generation)
            throw new InvalidOperationException("Prepared post output no longer owns its target generation.");

        // Metal binds uniforms from the segment captured by THIS recording. The prefix's upload
        // belongs to a different segment, so the final recording must publish the frozen payload again.
        if (refreshUniform)
        {
            FinalUbo uniforms = output.Uniforms;
            commands.UpdateBuffer(_finalBuf, 0, in uniforms);
        }
        commands.SetFramebuffer(target);
        commands.ClearColorTarget(0, output.Clear);
        commands.SetPipeline(_blitPipe);
        commands.SetGraphicsResourceSet(0, output.Blit);
        commands.Draw(3);
    }
}
