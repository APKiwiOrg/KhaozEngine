using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE PIPELINE-LEVEL OBJECTS DECISION R6 KEEPS A REDUNDANCY CACHE FOR, one member per cache slot in
    /// <see cref="D3D11DeviceState"/>. Six object slots, because primitive topology is a VALUE rather than an
    /// object and is cached in its own field: putting it here would mean boxing a topology on every pipeline
    /// bind, which is an allocation per draw on the hot path this cache exists to make free.
    /// <para>
    /// These are the objects a D3D11 pipeline is made of, and they are cached SEPARATELY rather than by pipeline
    /// identity on purpose. Two different pipelines routinely share a blend state, a depth-stencil state or an
    /// input layout, so a cache keyed on the pipeline would issue seven native calls for a switch that actually
    /// changed one shader. Per-object identity is what makes 5.3's claim, that a rebind to the same state costs
    /// nothing, true of a partial change and not only of an identical one.
    /// </para>
    /// </summary>
    internal enum D3D11StateSlot
    {
        /// <summary><c>VSSetShader</c>.</summary>
        VertexShader = 0,
        /// <summary><c>PSSetShader</c>.</summary>
        PixelShader = 1,
        /// <summary><c>OMSetBlendState</c>.</summary>
        BlendState = 2,
        /// <summary><c>OMSetDepthStencilState</c>.</summary>
        DepthStencilState = 3,
        /// <summary><c>RSSetState</c>.</summary>
        RasterizerState = 4,
        /// <summary><c>IASetInputLayout</c>.</summary>
        InputLayout = 5,
    }

    /// <summary>
    /// WHAT A BIND OR A SCRUB ACTUALLY CHANGED, so the caller issues exactly the native calls the change earned
    /// and nothing else. The seven pipeline-level flags mirror <see cref="D3D11StateSlot"/> plus the topology,
    /// and <see cref="Framebuffer"/> exists only for the scrub of decision R8, since a bound framebuffer is not a
    /// pipeline object and is never part of a pipeline bind.
    /// <para>
    /// A flags enum rather than a list, because the two consumers both want a fixed-order walk with no
    /// allocation: an emitter turning a change into native calls, and disposal turning a scrub into precise
    /// unbinds. R8's whole point is that a disposal unbinds WHAT IT HAS TO and never reaches for a wholesale
    /// <c>ClearState</c>, which needs the answer to "which slots named this resource" to be exact.
    /// </para>
    /// </summary>
    [Flags]
    internal enum D3D11StateChange
    {
        /// <summary>Nothing changed. The redundant rebind of R6, and the value that means zero native calls.</summary>
        None = 0,

        /// <summary><see cref="D3D11StateSlot.VertexShader"/>.</summary>
        VertexShader = 1 << 0,
        /// <summary><see cref="D3D11StateSlot.PixelShader"/>.</summary>
        PixelShader = 1 << 1,
        /// <summary><see cref="D3D11StateSlot.BlendState"/>.</summary>
        BlendState = 1 << 2,
        /// <summary><see cref="D3D11StateSlot.DepthStencilState"/>.</summary>
        DepthStencilState = 1 << 3,
        /// <summary><see cref="D3D11StateSlot.RasterizerState"/>.</summary>
        RasterizerState = 1 << 4,
        /// <summary><see cref="D3D11StateSlot.InputLayout"/>.</summary>
        InputLayout = 1 << 5,

        /// <summary>The primitive topology, cached as a value beside the six object slots.</summary>
        PrimitiveTopology = 1 << 6,

        /// <summary>The bound framebuffer, which only a scrub can report. A framebuffer bind answers with a plain
        /// bool, because decision W6 turns on one question: did the framebuffer CHANGE.</summary>
        Framebuffer = 1 << 7,
    }
}
