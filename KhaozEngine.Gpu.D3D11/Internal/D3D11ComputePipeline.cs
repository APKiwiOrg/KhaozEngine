using System;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// <see cref="IGpuComputePipeline"/> for the native Direct3D 11 backend, and the whole of what a compute
    /// pipeline is on this API: one compiled module plus the resource layouts that number its registers.
    /// <para>
    /// IT CREATES NOTHING NATIVE, and that is a fact about Direct3D 11 rather than a shortcut. A graphics pipeline
    /// owns four state objects and an input layout because the fixed-function stages need them, and a dispatch has
    /// no fixed-function stages at all: <c>CSSetShader</c> takes the module the shader path already created, and
    /// the register numbering is CPU-side arithmetic over the layouts. So the eager-creation rule of decision X1 is
    /// satisfied here by there being nothing to defer, and the module's own lifetime belongs to the
    /// <see cref="IGpuComputeShader"/> the caller created and disposes, exactly as a graphics pipeline never
    /// disposes its shader set.
    /// </para>
    /// <para>
    /// IT ANSWERS TWO INTERNAL SEAMS AND NEITHER IS <see cref="ID3D11PipelineState"/>. The emitter reads
    /// <see cref="ID3D11ComputePipelineState"/> for the module to bind, and the bind flush reads
    /// <see cref="ID3D11PipelineLayouts"/> for the layout array a compute set's registers are numbered against.
    /// Those are separate interfaces precisely so this type does not have to answer the seven graphics members it
    /// has no answer for, which is what <see cref="ID3D11PipelineLayouts"/>'s own remarks say.
    /// </para>
    /// <para>
    /// THERE IS STILL NO REDUNDANCY CACHE FOR THE COMPUTE SHADER, and that is now a decision rather than a
    /// deferral. A frame binds a graphics pipeline hundreds of times and a compute pipeline a handful, so the cache
    /// slot would pay a reference compare per dispatch to save a call the profile never shows, and
    /// <see cref="D3D11DeviceState"/>'s slot array is keyed by <see cref="D3D11StateSlot"/>, which would have to
    /// grow a member the graphics path then compares on every pipeline bind. The day a consumer dispatches per
    /// object it becomes worth measuring, and the shape to add is one more slot plus one more flag.
    /// </para>
    /// <para>
    /// WINDOWS-ONLY AT THE TYPE LEVEL, like every other wrapper here, because it holds a
    /// <see cref="D3D11ComputeShader"/>. That is a reference field, so nothing resolves the Vortice assembly by
    /// loading this type, and the load-path guard stays satisfied.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11ComputePipeline : IGpuComputePipeline, ID3D11ComputePipelineState,
        ID3D11PipelineLayouts
    {
        internal D3D11ComputePipeline(in GpuComputePipelineDescription description)
        {
            if (description.Shader is not D3D11ComputeShader shader)
            {
                throw new ArgumentException(
                    "A compute pipeline for the native Direct3D 11 backend needs a compute shader this backend "
                    + "compiled. A shader from another backend holds another backend's compiled module, and "
                    + "CSSetShader takes an ID3D11ComputeShader.",
                    nameof(description));
            }

            Shader = shader;
            ResourceLayouts = D3D11ResourceLayout.RequireAll(description.ResourceLayouts, "compute");
        }

        /// <summary>The compiled module this pipeline binds. Owned by the caller, never disposed here.</summary>
        internal D3D11ComputeShader Shader { get; }

        /// <summary>The resource layouts in PIPELINE-ARRAY order, which is the order decision S2 flattens the sets
        /// in. A compute set bound at slot k indexes this array.</summary>
        internal D3D11ResourceLayout[] ResourceLayouts { get; }

        /// <summary>True once disposed. Nothing native is released, because nothing native was created.</summary>
        internal bool IsDisposed { get; private set; }

        /// <inheritdoc/>
        object? ID3D11ComputePipelineState.ComputeShader => Shader.Shader;

        /// <inheritdoc/>
        D3D11ResourceLayout[] ID3D11PipelineLayouts.ResourceLayouts => ResourceLayouts;

        public void Dispose() => IsDisposed = true;
    }
}
