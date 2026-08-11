using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE SEAM'S <see cref="IGpuComputePipeline"/> ON THE NATIVE METAL BACKEND: one
    /// <c>MTLComputePipelineState</c>, the declared layouts, and the workgroup size a dispatch needs. Work
    /// breakdown row 11 (https://github.com/APKiwiOrg/KhaozEngine/issues/577).
    ///
    /// <para><b>IT IS THE GRAPHICS PIPELINE WITH ALMOST EVERYTHING REMOVED, and the list of what is gone is the
    /// whole of what makes it a separate type.</b> No vertex descriptor, so M-B2 has nothing to keep apart on
    /// this stage: the compute stage's buffer table holds only resource buffers and vertex streams cannot reach
    /// it. No attachment formats, no blend state, no depth-stencil state, no rasterizer state and no topology.
    /// What survives is the layout SHAPE check, which matters here for the same reason it matters there.</para>
    ///
    /// <para><b>THE WORKGROUP SIZE COMES OFF THE SHADER AND IS CARRIED HERE because Metal needs it at the
    /// DISPATCH.</b> <c>-dispatchThreadgroups:threadsPerThreadgroup:</c> takes the group size as an argument,
    /// where Direct3D 11 and Vulkan read it out of the compiled module, so this is the one backend where the
    /// number has to travel from the shader to the draw path. Row 9 read it out of the SPIR-V rather than taking
    /// it from a description nothing validates (which is what the incumbent does through
    /// <c>ComputePipelineDescription.ThreadGroupSize*</c>), and this hands the same numbers on to row 14
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580).</para>
    ///
    /// <para><b>NO DESCRIPTOR OBJECT IS INVOLVED AT ALL, and <see cref="MTLComputePipelineState"/> carries that
    /// argument in full.</b> The short version: the descriptor exists to set per-buffer MUTABILITY, the
    /// incumbent fills it by walking the layouts with a per-kind counter, and that counter is the
    /// declaration-order arithmetic section 2.2b forbids on a shipped path. The default infers the same
    /// mutability from the function's own <c>const device</c> and <c>constant</c> qualifiers.</para>
    /// </summary>
    internal sealed class MetalComputePipeline : IGpuComputePipeline, IMetalOwnedResource
    {
        readonly IDeviceLiveness _liveness;

        // The handle, held in a field because the property refuses once disposed and ReleaseOnMacOs runs AFTER
        // the flag flips. Disposal is the one reader that must still see it.
        readonly MTLComputePipelineState _state;

        /// <param name="liveness">The creating device's token, which is its identity.</param>
        /// <param name="shader">The compiled compute shader, which carries the function, the binding table and
        /// the workgroup size.</param>
        /// <param name="layouts">The declared resource layouts, in set order, already checked.</param>
        /// <param name="state">The <c>MTLComputePipelineState</c> at +1, or nil in a device-free test.</param>
        internal MetalComputePipeline(IDeviceLiveness liveness, MetalComputeShader shader,
            MetalResourceLayout[] layouts, MTLComputePipelineState state)
        {
            ArgumentNullException.ThrowIfNull(liveness);
            ArgumentNullException.ThrowIfNull(shader);
            ArgumentNullException.ThrowIfNull(layouts);

            _liveness = liveness;
            Shader = shader;
            Layouts = layouts;
            _state = state;
        }

        /// <summary>The name every refusal from this half of the row quotes.</summary>
        internal const string Label = "A native Metal compute pipeline";

        /// <inheritdoc/>
        public IDeviceLiveness Owner => _liveness;

        /// <summary>The compiled kernel, which is where the function, the binding table and the workgroup size
        /// come from.</summary>
        internal MetalComputeShader Shader { get; }

        /// <summary>The declared resource layouts, in set order. A set bound at slot k indexes this array.</summary>
        internal MetalResourceLayout[] Layouts { get; }

        /// <summary>
        /// The compiled pipeline state, bound with <c>-setComputePipelineState:</c>.
        /// <para>
        /// IT THROWS ONCE DISPOSED RATHER THAN ANSWERING, which is <c>MetalShaderSet.FunctionFor</c>'s precedent
        /// and for its reason: <see cref="Dispose"/> RELEASES this object, so handing the pointer back would set
        /// a released <c>MTLComputePipelineState</c> on a live compute encoder, which is a use-after-free inside
        /// the driver rather than anything this backend could report.
        /// </para>
        /// </summary>
        /// <exception cref="ObjectDisposedException">This pipeline is disposed.</exception>
        internal MTLComputePipelineState State
        {
            get
            {
                if (IsDisposed)
                {
                    throw new ObjectDisposedException(
                        nameof(MetalComputePipeline),
                        Label + "'s State was read after the pipeline was disposed. Disposal released the "
                        + "MTLComputePipelineState, so what is left is a pointer to an object the device has "
                        + "already let go of.");
                }

                return _state;
            }
        }

        /// <summary>The binding table row 13 binds through and compares by reference on a switch (M-R9).</summary>
        internal MetalShaderIndexTable Table => Shader.Table;

        /// <summary>How many resource-set slots this pipeline declares.</summary>
        internal int ResourceSlotCount => Layouts.Length;

        /// <summary>True once disposed.</summary>
        internal bool IsDisposed { get; private set; }

        /// <summary>
        /// THE ACCEPT PATH's WHOLE CHECK, the compute sibling of <c>MetalGraphicsPipeline.Require</c>: the right
        /// backend, the right device, and NOT DISPOSED. The disposal arm carries the same reason, with the same
        /// one object behind it rather than two.
        /// </summary>
        /// <param name="pipeline">The seam pipeline the caller passed.</param>
        /// <param name="owner">The calling device's liveness token, which is its identity.</param>
        /// <param name="parameterName">The entry point's own parameter name, for the exception.</param>
        /// <exception cref="ArgumentNullException">No pipeline.</exception>
        /// <exception cref="ArgumentException">A pipeline from another backend or another device.</exception>
        /// <exception cref="ObjectDisposedException">A disposed pipeline.</exception>
        internal static MetalComputePipeline Require(IGpuComputePipeline? pipeline, IDeviceLiveness owner,
            string parameterName)
        {
            ArgumentNullException.ThrowIfNull(pipeline, parameterName);

            MetalComputePipeline typed = MetalResourceOwnership.Require<MetalComputePipeline>(
                pipeline, owner, parameterName);

            if (typed.IsDisposed)
            {
                throw new ObjectDisposedException(
                    nameof(MetalComputePipeline),
                    Label + " that is already disposed was bound. Its MTLComputePipelineState has been released, "
                    + "so recording it would leave the dispatch that flushes it setting a released object on an "
                    + "encoder.");
            }

            return typed;
        }

        /// <summary>
        /// Build a compute pipeline on <paramref name="device"/>: check the declaration device-free first, then
        /// make the one native call.
        /// </summary>
        /// <exception cref="ArgumentException">No compute shader, or a shader or a layout from another backend or
        /// another device.</exception>
        /// <exception cref="ObjectDisposedException">A disposed resource layout.</exception>
        /// <exception cref="ShaderValidationException">The declared layout array is a different shape from the
        /// shader's reflection, or Metal rejected the function.</exception>
        [SupportedOSPlatform("macos")]
        internal static MetalComputePipeline Create(MTLDevice device, IDeviceLiveness liveness,
            in GpuComputePipelineDescription description)
        {
            (MetalComputeShader shader, MetalResourceLayout[] layouts) = Check(liveness, description);
            return CreateOnMacOs(device, liveness, shader, layouts);
        }

        /// <summary>
        /// THE DEVICE-FREE HALF, separated for the reason <see cref="MetalGraphicsPipelinePlan"/> gives at
        /// length: every refusal here is a fact about managed data, and folding them into the member that also
        /// calls <c>-newComputePipelineStateWithFunction:error:</c> would assert them on one leg out of five.
        /// </summary>
        internal static (MetalComputeShader Shader, MetalResourceLayout[] Layouts) Check(
            IDeviceLiveness liveness, in GpuComputePipelineDescription description)
        {
            ArgumentNullException.ThrowIfNull(liveness);

            if (description.Shader is null)
            {
                throw new ArgumentException(
                    Label + " was given no compute shader. A compute pipeline IS a kernel function plus the "
                    + "layouts it binds through, so there is nothing to build one from.",
                    nameof(description));
            }

            MetalComputeShader shader = MetalResourceOwnership.Require<MetalComputeShader>(
                description.Shader, liveness, nameof(description));

            // THE DEVICE-FREE HALF OF THE DISPOSED-SHADER REFUSAL, for the reason
            // MetalGraphicsPipelinePlan.Build gives at its own copy: MetalComputeShader.Function throws for a
            // disposed shader and nothing reaches it off macOS, so a refusal this type claims runs on one leg
            // out of five unless it is asked here.
            if (shader.IsDisposed)
            {
                throw new ObjectDisposedException(
                    nameof(MetalComputeShader),
                    Label + " was given a native Metal compute shader that is already disposed. Its MTLFunction "
                    + "and MTLLibrary have been released, so the pipeline would be created from a function the "
                    + "device has already let go of.");
            }

            IGpuResourceLayout[] declaredLayouts = description.ResourceLayouts ?? [];
            var layouts = new MetalResourceLayout[declaredLayouts.Length];
            var declared = new GpuResourceLayoutDescription[declaredLayouts.Length];
            for (int i = 0; i < declaredLayouts.Length; i++)
            {
                layouts[i] = MetalResourceLayout.Require(declaredLayouts[i], liveness, Label);
                declared[i] = layouts[i].Description;
            }

            // PIN 4 AGAIN, on the compute stage. The same wrong-pixel-no-error class reaches a compute kernel as
            // a wrong BUFFER, which is worse rather than better: a dispatch writing through a storage buffer
            // resolved from another declaration corrupts memory the next pass reads.
            shader.Table.RequireLayoutShape(declared, Label);

            return (shader, layouts);
        }

        /// <inheritdoc/>
        /// <remarks>Releases the state object, once, and never on a dead device (M-F6).</remarks>
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            if (_liveness.IsDead) return;
            if (!KhaozEngineMetal.IsPlatformSupported) return;

            ReleaseOnMacOs();
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static MetalComputePipeline CreateOnMacOs(MTLDevice device, IDeviceLiveness liveness,
            MetalComputeShader shader, MetalResourceLayout[] layouts)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            // Reads the function through the shader's own disposal guard, which throws by name for a shader that
            // has already released it.
            MTLFunction function = shader.Function;

            MTLComputePipelineState state = device.NewComputePipelineState(function, out NSError error);
            if (state.IsNull)
            {
                throw new ShaderValidationException(
                    "The native Metal device rejected this compute pipeline: "
                    + (error.IsNull
                        ? "-newComputePipelineStateWithFunction:error: answered nil and wrote no NSError, which "
                            + "means the failure is not a compatibility one at all."
                        : error.LocalizedDescription())
                    + " The function itself already compiled, so this is Metal refusing to build a pipeline out "
                    + "of it rather than a syntax error in the emitted MSL.");
            }

            return new MetalComputePipeline(liveness, shader, layouts, state);
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ReleaseOnMacOs()
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            // The field, not the property: IsDisposed is already true by the time this runs, so the property
            // would refuse the one caller that is entitled to the handle.
            _state.Release();
        }
    }
}
