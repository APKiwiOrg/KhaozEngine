using System;
using System.Runtime.Versioning;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE REAL EMITTER'S <see cref="ID3D11BindSink"/> HALF: the four array calls decision R6's fan-out asks for,
    /// each one span of engine binds transposed into the native arrays Direct3D takes and issued once.
    /// <para>
    /// WHICH METHOD A STAGE PICKS IS NOT DECIDED HERE. Every one of these switches on what
    /// <see cref="D3D11NativeCallName"/> answered, which is the SAME function
    /// <see cref="D3D11NativeTraceEmitter"/> writes into its trace. So the stage mapping is one implementation
    /// rather than two, a device-free budget measures the mapping this file uses, and what is left over is one
    /// step narrower than the package's remarks used to claim: not "which stage's method", but "does the arm for
    /// <c>PSSetSamplers</c> call <c>PSSetSamplers</c>". That is what decision T3's WARP <c>[GpuFact]</c> guards,
    /// and it is the smallest residue this shape can be reduced to without a device on the machine.
    /// </para>
    /// <para>
    /// THE UNORDERED-ACCESS REFUSAL COMES FREE with that, and it is issue #490 rather than a gap here. Direct3D
    /// 11 has no per-stage unordered-access setter outside compute: a pixel-shader UAV is bound through
    /// <c>OMSetRenderTargetsAndUnorderedAccessViews</c> alongside the render targets, which the framebuffer bind
    /// would have to carry, and no shipped layout declares one. The name resolution throws for every non-compute
    /// stage, so this file inherits the refusal by asking rather than by re-deciding it, and deliberately does
    /// NOT implement the render-target-and-UAV path.
    /// </para>
    /// <para>
    /// ZERO PER-CALL ALLOCATION: every array here is scratch owned by <see cref="D3D11EmitterContext"/>, grown
    /// to the widest bind the process has seen and reused. The spans arriving are the ones
    /// <see cref="D3D11SetActivation"/> filled, so the whole path from a resource set to a native call allocates
    /// nothing after the first frame.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal readonly partial struct D3D11NativeEmitter
    {
        /// <inheritdoc/>
        public void SetConstantBuffers(GpuShaderStages stage, uint startSlot,
            ReadOnlySpan<D3D11ConstantBufferBind> binds)
        {
            if (binds.Length == 0) return;

            // Resolved BEFORE the name, so a malformed bind is refused with its own message rather than after a
            // stage lookup that would have thrown a less specific one.
            ID3D11Buffer?[] buffers = _context.ConstantBuffers(binds.Length);
            D3D11BindResolve.Constants(binds, _context.FirstConstants, _context.ConstantCounts);
            for (int i = 0; i < binds.Length; i++)
            {
                buffers[i] = (ID3D11Buffer?)D3D11BindResolve.ViewOf(
                    binds[i].Buffer, D3D11RegisterFile.ConstantBuffer);
            }

            IssueConstantBuffers(D3D11NativeCallName.ConstantBuffers(stage), startSlot, binds.Length);
        }

        /// <summary>
        /// DECISION R7's <c>!DriverCommandLists</c> WORKAROUND: the same span unbound immediately before the bind
        /// that follows it, on a driver whose runtime EMULATES command lists. It is the same native method with a
        /// null in every entry, which is what makes the following bind a genuine change rather than one the
        /// runtime coalesces away.
        /// </summary>
        public void UnsetConstantBuffers(GpuShaderStages stage, uint startSlot, int count)
        {
            if (count <= 0) return;

            ID3D11Buffer?[] buffers = _context.ConstantBuffers(count);
            int[] firstConstants = _context.FirstConstants;
            int[] constantCounts = _context.ConstantCounts;
            for (int i = 0; i < count; i++)
            {
                buffers[i] = null;
                firstConstants[i] = 0;
                constantCounts[i] = 0;
            }

            IssueConstantBuffers(D3D11NativeCallName.ConstantBuffers(stage), startSlot, count);
        }

        /// <inheritdoc/>
        public void SetShaderResources(GpuShaderStages stage, uint startSlot,
            ReadOnlySpan<IGpuBindableResource?> resources)
        {
            if (resources.Length == 0) return;

            ID3D11ShaderResourceView?[] views = _context.ShaderResources(resources.Length);
            for (int i = 0; i < resources.Length; i++)
            {
                views[i] = (ID3D11ShaderResourceView?)D3D11BindResolve.ViewOf(
                    resources[i], D3D11RegisterFile.ShaderResource);
            }

            int slot = (int)startSlot;
            switch (D3D11NativeCallName.ShaderResources(stage))
            {
                case D3D11NativeCall.VSSetShaderResources:
                    Native.VSSetShaderResources(slot, resources.Length, views!);
                    return;
                case D3D11NativeCall.HSSetShaderResources:
                    Native.HSSetShaderResources(slot, resources.Length, views!);
                    return;
                case D3D11NativeCall.DSSetShaderResources:
                    Native.DSSetShaderResources(slot, resources.Length, views!);
                    return;
                case D3D11NativeCall.GSSetShaderResources:
                    Native.GSSetShaderResources(slot, resources.Length, views!);
                    return;
                case D3D11NativeCall.PSSetShaderResources:
                    Native.PSSetShaderResources(slot, resources.Length, views!);
                    return;
                case D3D11NativeCall.CSSetShaderResources:
                    Native.CSSetShaderResources(slot, resources.Length, views!);
                    return;
                default:
                    throw Unreachable(stage, "shader resources");
            }
        }

        /// <inheritdoc/>
        public void SetSamplers(GpuShaderStages stage, uint startSlot, ReadOnlySpan<IGpuBindableResource?> samplers)
        {
            if (samplers.Length == 0) return;

            ID3D11SamplerState?[] states = _context.Samplers(samplers.Length);
            for (int i = 0; i < samplers.Length; i++)
                states[i] = (ID3D11SamplerState?)D3D11BindResolve.ViewOf(samplers[i], D3D11RegisterFile.Sampler);

            int slot = (int)startSlot;
            switch (D3D11NativeCallName.Samplers(stage))
            {
                case D3D11NativeCall.VSSetSamplers:
                    Native.VSSetSamplers(slot, samplers.Length, states!);
                    return;
                case D3D11NativeCall.HSSetSamplers:
                    Native.HSSetSamplers(slot, samplers.Length, states!);
                    return;
                case D3D11NativeCall.DSSetSamplers:
                    Native.DSSetSamplers(slot, samplers.Length, states!);
                    return;
                case D3D11NativeCall.GSSetSamplers:
                    Native.GSSetSamplers(slot, samplers.Length, states!);
                    return;
                case D3D11NativeCall.PSSetSamplers:
                    Native.PSSetSamplers(slot, samplers.Length, states!);
                    return;
                case D3D11NativeCall.CSSetSamplers:
                    Native.CSSetSamplers(slot, samplers.Length, states!);
                    return;
                default:
                    throw Unreachable(stage, "samplers");
            }
        }

        /// <summary>
        /// <c>CSSetUnorderedAccessViews</c>, and compute is the only stage that reaches it: the name resolution
        /// refuses every other one by name (issue #490), so a graphics layout declaring a <c>u</c> binding fails
        /// with the mechanism written out rather than binding nothing.
        /// <para>
        /// The initial-counts array is omitted, which leaves every append or consume counter untouched at -1.
        /// No shipped layout uses an append buffer, and the two compute layouts that reach the <c>u</c> file at
        /// all are plain read-write structured buffers.
        /// </para>
        /// </summary>
        public void SetUnorderedAccessViews(GpuShaderStages stage, uint startSlot,
            ReadOnlySpan<IGpuBindableResource?> views)
        {
            if (views.Length == 0) return;

            D3D11NativeCall call = D3D11NativeCallName.UnorderedAccessViews(stage);
            ID3D11UnorderedAccessView?[] resolved = _context.UnorderedAccessViews(views.Length);
            for (int i = 0; i < views.Length; i++)
            {
                resolved[i] = (ID3D11UnorderedAccessView?)D3D11BindResolve.ViewOf(
                    views[i], D3D11RegisterFile.UnorderedAccess);
            }

            if (call != D3D11NativeCall.CSSetUnorderedAccessViews) throw Unreachable(stage, "unordered access");

            Native.CSSetUnorderedAccessViews((int)startSlot, views.Length, resolved!);
        }

        // The one place a resolved call name turns into a *SetConstantBuffers1, shared by the bind and by the
        // unset-before-set workaround so the two cannot pick different methods for one stage.
        void IssueConstantBuffers(D3D11NativeCall call, uint startSlot, int count)
        {
            ID3D11Buffer?[] buffers = _context.ConstantBuffers(count);
            int[] firstConstants = _context.FirstConstants;
            int[] constantCounts = _context.ConstantCounts;
            int slot = (int)startSlot;

            switch (call)
            {
                case D3D11NativeCall.VSSetConstantBuffers1:
                    Native.VSSetConstantBuffers1(slot, count, buffers!, firstConstants, constantCounts);
                    return;
                case D3D11NativeCall.HSSetConstantBuffers1:
                    Native.HSSetConstantBuffers1(slot, count, buffers!, firstConstants, constantCounts);
                    return;
                case D3D11NativeCall.DSSetConstantBuffers1:
                    Native.DSSetConstantBuffers1(slot, count, buffers!, firstConstants, constantCounts);
                    return;
                case D3D11NativeCall.GSSetConstantBuffers1:
                    Native.GSSetConstantBuffers1(slot, count, buffers!, firstConstants, constantCounts);
                    return;
                case D3D11NativeCall.PSSetConstantBuffers1:
                    Native.PSSetConstantBuffers1(slot, count, buffers!, firstConstants, constantCounts);
                    return;
                case D3D11NativeCall.CSSetConstantBuffers1:
                    Native.CSSetConstantBuffers1(slot, count, buffers!, firstConstants, constantCounts);
                    return;
                default:
                    throw Unreachable(GpuShaderStages.None, "constant buffers");
            }
        }

        // Unreachable while the name resolution and these switches agree, which is what makes it worth throwing:
        // it fires the day a stage is added to one and not the other, instead of a bind landing nowhere.
        static InvalidOperationException Unreachable(GpuShaderStages stage, string what)
            => new($"The native Direct3D 11 emitter has no {what} arm for the call D3D11NativeCallName resolved "
                + $"from the {stage} stage. The two are meant to enumerate the same set, so this is a stage that "
                + "gained a name without gaining a call.");
    }
}
