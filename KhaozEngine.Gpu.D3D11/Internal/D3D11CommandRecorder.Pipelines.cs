using System.Runtime.CompilerServices;

namespace KhaozEngine.Gpu.D3D11.Internal;

internal sealed partial class D3D11CommandRecorder<TEmitter>
{
    // These bindings belong to the list's recording, independently of the emitter's device state.
    // Pass and framebuffer changes keep them. Begin forgets both before either driver can record work.
    bool _graphicsPipelineBound;
    bool _computePipelineBound;

    void RequirePipeline(bool compute, [CallerMemberName] string command = "")
    {
        RequireRecording(command);
        if (compute ? _computePipelineBound : _graphicsPipelineBound) return;

        throw new GpuRecordingStateException(
            compute ? GpuRecordingStateRefusal.ComputePipelineNotBound : GpuRecordingStateRefusal.GraphicsPipelineNotBound,
            command + " requires a " + (compute ? "compute" : "graphics")
                + " pipeline bound since Begin. Call " + (compute ? "SetComputePipeline" : "SetPipeline") + " first.")
        { Backend = GpuBackendKind.Direct3D11Native, Command = command };
    }
}
