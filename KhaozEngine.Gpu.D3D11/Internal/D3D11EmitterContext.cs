using System;
using System.Runtime.Versioning;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE DEVICE CONTEXT AND THE SCRATCH THE REAL EMITTER FILLS, and the class reference that lets that emitter
    /// be a readonly struct. One per device, beside the one <see cref="D3D11DeviceState"/>, handed to every
    /// emitter value the device makes.
    /// <para>
    /// WHY THE SCRATCH IS HERE RATHER THAN IN THE EMITTER. Decision R6 turns a bind into ONE array call per
    /// register file per stage, and every one of those calls takes a native array. Allocating them per call would
    /// put a garbage-collected allocation on the hottest path the backend has, which is the cost the whole
    /// batching argument exists to remove. So the arrays are owned here, grown geometrically to the widest bind
    /// the process has seen, and reused. That is safe for exactly the reason
    /// <see cref="D3D11SetActivation"/>'s scratch is: decision W5 has one thread recording at a time, and the
    /// deferred driver's replay runs under the one submit lock.
    /// </para>
    /// <para>
    /// EVERY FIELD IS A REFERENCE, and that is load-bearing rather than incidental. A Vortice VALUE-TYPE field
    /// anywhere in this package makes the CLR resolve the interop assembly the moment the declaring type is
    /// loaded, and the suite loads every type in the package by reflection, so one <c>Format</c> or
    /// <c>Viewport</c> field here would turn every load-path assertion red on macOS at once. Arrays and COM
    /// interfaces are pointers and cost nothing. The value-typed arguments a call needs (a viewport, a scissor
    /// rect, a blend factor) are built as LOCALS inside the emitter's Windows-only bodies, which are never JITted
    /// on a platform that has no device.
    /// </para>
    /// <para>
    /// THE CONTEXT IS AN <c>ID3D11DeviceContext1</c> rather than the base interface, because decision R7 routes
    /// every constant-buffer bind through <c>*SetConstantBuffers1</c> and those six methods live on the
    /// versioned interface. The device queries for it once at creation, where a runtime too old to answer is a
    /// refusal with a message rather than a cast that fails per draw.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11EmitterContext
    {
        // Eight covers every shipped bind: the widest set in the engine is seven elements and no register file
        // inside it spans more than four. The growth curve is shared with D3D11SetActivation so the two scratches
        // widen together rather than on two different rules.
        ID3D11Buffer?[] _constantBuffers = new ID3D11Buffer?[8];
        int[] _firstConstants = new int[8];
        int[] _constantCounts = new int[8];

        ID3D11ShaderResourceView?[] _shaderResources = new ID3D11ShaderResourceView?[8];
        ID3D11SamplerState?[] _samplers = new ID3D11SamplerState?[8];
        ID3D11UnorderedAccessView?[] _unorderedAccessViews = new ID3D11UnorderedAccessView?[8];

        ID3D11RenderTargetView?[] _renderTargets = new ID3D11RenderTargetView?[8];

        ID3D11Buffer?[] _vertexBuffers = new ID3D11Buffer?[8];
        int[] _vertexStrides = new int[8];
        int[] _vertexOffsets = new int[8];

        /// <summary>Build the device's one emitter context over its immediate context.</summary>
        internal D3D11EmitterContext(ID3D11DeviceContext1 context)
            => Context = context ?? throw new ArgumentNullException(nameof(context));

        /// <summary>The immediate context every call goes to.</summary>
        internal ID3D11DeviceContext1 Context { get; }

        /// <summary>The constant-buffer scratch, grown to <paramref name="count"/> entries along with
        /// <see cref="FirstConstants"/> and <see cref="ConstantCounts"/>, which the same call consumes in
        /// parallel. Contents are undefined on entry: every entry in the span is written before the call.
        /// </summary>
        internal ID3D11Buffer?[] ConstantBuffers(int count)
        {
            if (_constantBuffers.Length >= count) return _constantBuffers;

            int capacity = D3D11BindResolve.RoundedCapacity(count);
            _constantBuffers = new ID3D11Buffer?[capacity];
            _firstConstants = new int[capacity];
            _constantCounts = new int[capacity];
            return _constantBuffers;
        }

        /// <summary>The first-constant array paired with <see cref="ConstantBuffers"/>.</summary>
        internal int[] FirstConstants => _firstConstants;

        /// <summary>The constant-count array paired with <see cref="ConstantBuffers"/>.</summary>
        internal int[] ConstantCounts => _constantCounts;

        /// <summary>The shader-resource scratch, grown to <paramref name="count"/> entries.</summary>
        internal ID3D11ShaderResourceView?[] ShaderResources(int count)
            => _shaderResources.Length >= count
                ? _shaderResources
                : _shaderResources = new ID3D11ShaderResourceView?[D3D11BindResolve.RoundedCapacity(count)];

        /// <summary>The sampler scratch, grown to <paramref name="count"/> entries.</summary>
        internal ID3D11SamplerState?[] Samplers(int count)
            => _samplers.Length >= count
                ? _samplers
                : _samplers = new ID3D11SamplerState?[D3D11BindResolve.RoundedCapacity(count)];

        /// <summary>The unordered-access scratch, grown to <paramref name="count"/> entries.</summary>
        internal ID3D11UnorderedAccessView?[] UnorderedAccessViews(int count)
            => _unorderedAccessViews.Length >= count
                ? _unorderedAccessViews
                : _unorderedAccessViews = new ID3D11UnorderedAccessView?[D3D11BindResolve.RoundedCapacity(count)];

        /// <summary>The render-target scratch, grown to <paramref name="count"/> entries.</summary>
        internal ID3D11RenderTargetView?[] RenderTargets(int count)
            => _renderTargets.Length >= count
                ? _renderTargets
                : _renderTargets = new ID3D11RenderTargetView?[D3D11BindResolve.RoundedCapacity(count)];

        /// <summary>
        /// DROP THE RENDER-TARGET SCRATCH'S REFERENCES after the bind, which the other scratches do not need to
        /// do. A swapchain resize releases the backbuffer's views and then calls <c>ResizeBuffers</c>, which fails
        /// on an outstanding reference, and this array is the one place in the backend that would hold a view
        /// after the call that consumed it. Clearing it costs a handful of stores on a path that runs a few times
        /// a frame rather than a few thousand.
        /// </summary>
        internal void ReleaseRenderTargets(int count) => Array.Clear(_renderTargets, 0, count);

        /// <summary>The vertex-stream scratch, grown to <paramref name="count"/> entries along with
        /// <see cref="VertexStrides"/> and <see cref="VertexOffsets"/>, which
        /// <c>IASetVertexBuffers</c> consumes in parallel.</summary>
        internal ID3D11Buffer?[] VertexBuffers(int count)
        {
            if (_vertexBuffers.Length >= count) return _vertexBuffers;

            int capacity = D3D11BindResolve.RoundedCapacity(count);
            _vertexBuffers = new ID3D11Buffer?[capacity];
            _vertexStrides = new int[capacity];
            _vertexOffsets = new int[capacity];
            return _vertexBuffers;
        }

        /// <summary>The stride array paired with <see cref="VertexBuffers"/>.</summary>
        internal int[] VertexStrides => _vertexStrides;

        /// <summary>The offset array paired with <see cref="VertexBuffers"/>.</summary>
        internal int[] VertexOffsets => _vertexOffsets;
    }
}
