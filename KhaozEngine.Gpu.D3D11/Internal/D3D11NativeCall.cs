namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE <c>ID3D11DeviceContext</c> CALLS THIS ROW CAN NAME, and a vocabulary kept strictly apart from
    /// <see cref="D3D11OpCode"/>. An opcode is a SEAM call, one per <see cref="IGpuCommandList"/> command. A
    /// member here is a NATIVE call, which is what decision T2's budget is actually made of, and the whole point
    /// of 5.3 is that the two counts differ: one seam-level resource-set bind is up to six native calls, a
    /// redundant pipeline bind is zero, and a redundant framebuffer re-bind is zero where a change is three.
    /// Sharing one enum between the two would make a seam tally readable as a native tally, which is the
    /// confusion the seam's own remarks exist to prevent.
    /// <para>
    /// Every member is a real <c>ID3D11DeviceContext</c> method name, with one deliberate exception named as such
    /// (<see cref="ResourceSetPending"/>). Names are the D3D11 ones rather than engine ones so a trace can be
    /// read straight against a capture or against the incumbent's call sequence.
    /// </para>
    /// </summary>
    internal enum D3D11NativeCall
    {
        /// <summary>Not a call. A zeroed value is recognisably empty rather than a valid call.</summary>
        None = 0,

        /// <summary>The ONE <c>ClearState</c> decision R3 puts at the head of every replay.</summary>
        ClearState = 1,

        /// <summary>Binding the render targets of a framebuffer. Decision W6 issues it on a framebuffer CHANGE
        /// only.</summary>
        OMSetRenderTargets = 2,
        /// <summary>The full viewport that comes with a framebuffer change, and the only place the backend sets a
        /// viewport at all, since the seam carries no <c>SetViewport</c> (9.4).</summary>
        RSSetViewports = 3,
        /// <summary>The scissor rectangles, whether the full ones a framebuffer change brings or an explicit one
        /// the seam asked for.</summary>
        RSSetScissorRects = 4,

        /// <summary>Clearing one colour attachment.</summary>
        ClearRenderTargetView = 5,
        /// <summary>Clearing the depth attachment.</summary>
        ClearDepthStencilView = 6,

        /// <summary>Binding the vertex shader of a pipeline.</summary>
        VSSetShader = 7,
        /// <summary>Binding the pixel shader of a pipeline.</summary>
        PSSetShader = 8,
        /// <summary>Binding a pipeline's blend state.</summary>
        OMSetBlendState = 9,
        /// <summary>Binding a pipeline's depth-stencil state.</summary>
        OMSetDepthStencilState = 10,
        /// <summary>Binding a pipeline's rasterizer state.</summary>
        RSSetState = 11,
        /// <summary>Binding a pipeline's input layout.</summary>
        IASetInputLayout = 12,
        /// <summary>Setting a pipeline's primitive topology.</summary>
        IASetPrimitiveTopology = 13,

        /// <summary>Binding vertex buffers.</summary>
        IASetVertexBuffers = 14,
        /// <summary>Binding the index buffer.</summary>
        IASetIndexBuffer = 15,

        /// <summary>A non-indexed draw. D3D11 has no non-instanced entry point, so the seam's single-instance
        /// draw arrives here as one instance, exactly as the incumbent issues it.</summary>
        DrawInstanced = 16,
        /// <summary>An indexed draw, likewise always through the instanced entry point.</summary>
        DrawIndexedInstanced = 17,

        /// <summary>A buffer or texture write from CPU memory.</summary>
        UpdateSubresource = 18,
        /// <summary>A whole-resource copy.</summary>
        CopyResource = 19,
        /// <summary>A partial copy, which is what a buffer range copy and a texture subresource copy both
        /// are.</summary>
        CopySubresourceRegion = 20,
        /// <summary>Generating a texture's mip chain.</summary>
        GenerateMips = 21,
        /// <summary>Resolving a multisampled target into a single-sample texture.</summary>
        ResolveSubresource = 22,

        /// <summary>Binding a compute shader.</summary>
        CSSetShader = 23,
        /// <summary>Dispatching workgroups.</summary>
        Dispatch = 24,

        /// <summary>
        /// NOT A NATIVE CALL, and the one member here that is not an <c>ID3D11DeviceContext</c> method. A
        /// resource-set bind RECORDS ONLY under decision R5's schedule and flushes at the next draw or dispatch,
        /// and that flush, its three-state dirty tracking and its array-batched fan-out into
        /// <c>*SetConstantBuffers1</c>, <c>*SetShaderResources</c> and <c>*SetSamplers</c> are the bind flush of
        /// work-breakdown row 9. This member holds the bind's PLACE IN THE ORDER so a trace taken here is still a
        /// faithful sequence, and it is named for what it is so nobody adds it into a native-call total.
        /// </summary>
        ResourceSetPending = 25,
    }
}
