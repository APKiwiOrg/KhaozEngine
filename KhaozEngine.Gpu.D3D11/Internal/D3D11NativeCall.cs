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
        /// so the bind itself reaches the device as nothing at all. This member holds its PLACE IN THE ORDER, so a
        /// trace shows where a set was recorded as well as where the flush issued it, and it is named for what it
        /// is. <see cref="D3D11NativeCallLog.TotalCalls"/> EXCLUDES it by name, because a budget made partly of
        /// non-calls is not a budget.
        /// </summary>
        ResourceSetPending = 25,

        // ---- The bind flush of decision R5, one array call per register file per stage (R6, R7) ----
        //
        // Six stages times three files, plus the compute-only unordered-access setter. Written out rather than
        // reduced to one member per file with the stage in the arguments, because the whole point of the budget is
        // to count calls per stage: "one VSSetConstantBuffers1" and "one PSSetShaderResources" are the assertions,
        // and a test that had to parse an argument string to make them would be pinning a format instead of a
        // count. D3D11NativeCallName is the one place a (file, stage) pair turns into one of these.

        /// <summary>Constant buffers for the vertex stage. ALWAYS the <c>1</c> overload (decision R7).</summary>
        VSSetConstantBuffers1 = 26,
        /// <summary>Constant buffers for the hull stage.</summary>
        HSSetConstantBuffers1 = 27,
        /// <summary>Constant buffers for the domain stage.</summary>
        DSSetConstantBuffers1 = 28,
        /// <summary>Constant buffers for the geometry stage.</summary>
        GSSetConstantBuffers1 = 29,
        /// <summary>Constant buffers for the pixel stage.</summary>
        PSSetConstantBuffers1 = 30,
        /// <summary>Constant buffers for the compute stage.</summary>
        CSSetConstantBuffers1 = 31,

        /// <summary>Shader resources for the vertex stage. Sampled textures and read-only structured buffers
        /// share the <c>t</c> file, so one call may carry both.</summary>
        VSSetShaderResources = 32,
        /// <summary>Shader resources for the hull stage.</summary>
        HSSetShaderResources = 33,
        /// <summary>Shader resources for the domain stage.</summary>
        DSSetShaderResources = 34,
        /// <summary>Shader resources for the geometry stage.</summary>
        GSSetShaderResources = 35,
        /// <summary>Shader resources for the pixel stage.</summary>
        PSSetShaderResources = 36,
        /// <summary>Shader resources for the compute stage.</summary>
        CSSetShaderResources = 37,

        /// <summary>Samplers for the vertex stage.</summary>
        VSSetSamplers = 38,
        /// <summary>Samplers for the hull stage.</summary>
        HSSetSamplers = 39,
        /// <summary>Samplers for the domain stage.</summary>
        DSSetSamplers = 40,
        /// <summary>Samplers for the geometry stage.</summary>
        GSSetSamplers = 41,
        /// <summary>Samplers for the pixel stage.</summary>
        PSSetSamplers = 42,
        /// <summary>Samplers for the compute stage.</summary>
        CSSetSamplers = 43,

        /// <summary>Unordered access views for the compute stage, which is the only stage Direct3D 11 binds them
        /// through a stage setter at all: a pixel-shader UAV rides
        /// <c>OMSetRenderTargetsAndUnorderedAccessViews</c> alongside the render targets.</summary>
        CSSetUnorderedAccessViews = 44,

        // ---- The uniform ring's two context calls (decisions U1 and U2) ----

        /// <summary>
        /// <c>Map</c> on a constant-buffer ring, always <c>MAP_WRITE_NO_OVERWRITE</c>. Made through
        /// <see cref="ID3D11RingMemory"/> rather than by any emitter, and named here so decision T2's structural
        /// invariant "zero <c>Map</c> or <c>Unmap</c> during replay" is a statement the budget's trace can
        /// actually carry. Without a member for it the invariant would pass by having no vocabulary to fail in.
        /// </summary>
        Map = 45,

        /// <summary>
        /// <c>Unmap</c> on a constant-buffer ring. Belongs at the head of a <c>Submit</c>, before the replay, and
        /// under <c>KE_D3D11_RECORD=immediate</c> at each bind-flush point, never between the
        /// <see cref="ClearState"/> and the end of a replay.
        /// </summary>
        Unmap = 46,
    }
}
