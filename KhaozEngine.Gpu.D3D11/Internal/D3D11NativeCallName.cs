using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// WHICH <c>ID3D11DeviceContext</c> METHOD A REGISTER FILE PLUS A STAGE NAMES, and the ONE thing the
    /// device-free native-call budget can drift from the real replay path in.
    /// <para>
    /// THAT IS THE SINK DECISION OF WORK-BREAKDOWN ROW 9, stated as a type. The schedule of decision R5 and the
    /// array-batched fan-out of decision R6 live in <see cref="D3D11BindFlush"/> and
    /// <see cref="D3D11SetActivation"/>, which are device-owned, device-free, and used UNCHANGED by the real
    /// emitter, so a budget taken over <see cref="D3D11NativeTraceEmitter"/> is a measurement of the shipped
    /// decisions. What is left over is the translation below: <c>Vertex</c> plus the <c>b</c> file is
    /// <c>VSSetConstantBuffers1</c>. Both emitters go through THIS function to make that translation, so even that
    /// is one implementation rather than two, and decision T3's WARP <c>[GpuFact]</c> guards the last step from
    /// the real emitter calling a differently-named Vortice method than the one it asked for.
    /// </para>
    /// <para>
    /// A stage with no setter for a file THROWS rather than falling back to a nearby one. There is exactly one
    /// such case on Direct3D 11 (unordered access outside compute) and the failure it would otherwise produce is a
    /// binding that silently lands nowhere.
    /// </para>
    /// </summary>
    internal static class D3D11NativeCallName
    {
        /// <summary>The <c>*SetConstantBuffers1</c> for one stage.</summary>
        internal static D3D11NativeCall ConstantBuffers(GpuShaderStages stage) => stage switch
        {
            GpuShaderStages.Vertex => D3D11NativeCall.VSSetConstantBuffers1,
            GpuShaderStages.TessellationControl => D3D11NativeCall.HSSetConstantBuffers1,
            GpuShaderStages.TessellationEvaluation => D3D11NativeCall.DSSetConstantBuffers1,
            GpuShaderStages.Geometry => D3D11NativeCall.GSSetConstantBuffers1,
            GpuShaderStages.Fragment => D3D11NativeCall.PSSetConstantBuffers1,
            GpuShaderStages.Compute => D3D11NativeCall.CSSetConstantBuffers1,
            _ => throw Unnamed(stage, "constant buffers"),
        };

        /// <summary>The <c>*SetShaderResources</c> for one stage.</summary>
        internal static D3D11NativeCall ShaderResources(GpuShaderStages stage) => stage switch
        {
            GpuShaderStages.Vertex => D3D11NativeCall.VSSetShaderResources,
            GpuShaderStages.TessellationControl => D3D11NativeCall.HSSetShaderResources,
            GpuShaderStages.TessellationEvaluation => D3D11NativeCall.DSSetShaderResources,
            GpuShaderStages.Geometry => D3D11NativeCall.GSSetShaderResources,
            GpuShaderStages.Fragment => D3D11NativeCall.PSSetShaderResources,
            GpuShaderStages.Compute => D3D11NativeCall.CSSetShaderResources,
            _ => throw Unnamed(stage, "shader resources"),
        };

        /// <summary>The <c>*SetSamplers</c> for one stage.</summary>
        internal static D3D11NativeCall Samplers(GpuShaderStages stage) => stage switch
        {
            GpuShaderStages.Vertex => D3D11NativeCall.VSSetSamplers,
            GpuShaderStages.TessellationControl => D3D11NativeCall.HSSetSamplers,
            GpuShaderStages.TessellationEvaluation => D3D11NativeCall.DSSetSamplers,
            GpuShaderStages.Geometry => D3D11NativeCall.GSSetSamplers,
            GpuShaderStages.Fragment => D3D11NativeCall.PSSetSamplers,
            GpuShaderStages.Compute => D3D11NativeCall.CSSetSamplers,
            _ => throw Unnamed(stage, "samplers"),
        };

        /// <summary>
        /// The unordered-access setter for one stage, which exists for COMPUTE alone. Direct3D 11 binds a
        /// pixel-shader UAV through <c>OMSetRenderTargetsAndUnorderedAccessViews</c> together with the render
        /// targets rather than through a per-stage setter, so a graphics layout declaring a <c>u</c> binding needs
        /// the framebuffer bind to carry it. No shipped layout does: the only two that reach the <c>u</c> file at
        /// all are <c>OceanFftProducer</c>'s, and both are compute.
        /// </summary>
        internal static D3D11NativeCall UnorderedAccessViews(GpuShaderStages stage) => stage switch
        {
            GpuShaderStages.Compute => D3D11NativeCall.CSSetUnorderedAccessViews,
            _ => throw new NotSupportedException(
                $"A resource layout declares an unordered-access binding visible to the {stage} stage. Direct3D 11 "
                + "has no per-stage setter for that outside compute: a pixel-shader unordered-access view is bound "
                + "through OMSetRenderTargetsAndUnorderedAccessViews alongside the render targets, which the "
                + "framebuffer bind would have to carry. No shipped layout declares one, so the path does not "
                + "exist yet and is refused here rather than binding nothing."),
        };

        static ArgumentOutOfRangeException Unnamed(GpuShaderStages stage, string what)
            => new(nameof(stage), stage,
                $"No Direct3D 11 setter for {what} names the stage '{stage}'. A bind fans out one call per "
                + "register file per SINGLE stage, so a combined mask or GpuShaderStages.None reaching here is a "
                + "defect in the fan-out rather than a layout a driver could refuse.");
    }
}
