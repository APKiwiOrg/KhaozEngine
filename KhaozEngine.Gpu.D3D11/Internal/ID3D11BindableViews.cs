namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// WHICH VIEW A BINDABLE RESOURCE OFFERS EACH REGISTER FILE, and the seam that lets the real emitter's
    /// resource-to-view resolution be a device-free <c>[Fact]</c>. The third internal capability seam in this
    /// package, after <see cref="ID3D11PipelineState"/> and <see cref="ID3D11RingBacked"/>, and it exists for the
    /// same two reasons.
    /// <para>
    /// FIRST, IT KEEPS THE RESOLUTION OFF WINDOWS. <see cref="D3D11Texture"/>, <see cref="D3D11Buffer"/> and
    /// <see cref="D3D11Sampler"/> are all <c>[SupportedOSPlatform("windows")]</c> at the type level and their view
    /// properties are typed Direct3D handles, so a resolver that named them could not be compiled into a body
    /// that runs everywhere. Every member here is <c>object</c>, exactly as the pipeline seam's are, so
    /// <see cref="D3D11BindResolve"/> can decide WHICH view a bind wants without naming a Direct3D type and the
    /// emitter is left with one cast at the call.
    /// </para>
    /// <para>
    /// SECOND, IT MAKES THE ANSWER THE RESOURCE'S RATHER THAN A SWITCH'S. A resolver written as a type switch
    /// over the three concrete classes silently answers null for a fourth, and the failure is a register bound to
    /// nothing rather than an exception. Asking the resource means a new bindable type either answers or does not
    /// implement the seam, and the refusal names it.
    /// </para>
    /// <para>
    /// A resource answers null for every file it has no view for, which is the ordinary case: a sampler has only
    /// a sampler state, a texture never has one, and a buffer has a shader-resource view only when its declared
    /// usage earned it. A null reaching a bind is refused by name at the resolver rather than passed through as a
    /// hole, because a hole is a register the layout deliberately left empty and this is a resource that cannot
    /// satisfy the register it was bound to.
    /// </para>
    /// </summary>
    internal interface ID3D11BindableViews
    {
        /// <summary>The <c>t</c>-file view (<c>ID3D11ShaderResourceView</c>), or null when this resource has
        /// none.</summary>
        object? ShaderResourceViewObject { get; }

        /// <summary>The <c>s</c>-file object (<c>ID3D11SamplerState</c>), or null.</summary>
        object? SamplerStateObject { get; }

        /// <summary>The <c>u</c>-file view (<c>ID3D11UnorderedAccessView</c>), or null.</summary>
        object? UnorderedAccessViewObject { get; }

        /// <summary>
        /// The native buffer (<c>ID3D11Buffer</c>), or null for anything that is not a buffer. Not a view at all,
        /// which is why it is named for what it is: a constant buffer binds the RESOURCE with a window rather
        /// than a view object, and the input assembler binds the same resource for a vertex or index stream.
        /// </summary>
        object? BufferObject { get; }
    }

    /// <summary>
    /// WHAT A FRAMEBUFFER BINDS AT THE OUTPUT MERGER, object-typed for the reason
    /// <see cref="ID3D11BindableViews"/> gives, and answered by BOTH framebuffer types this backend has.
    /// <para>
    /// THERE ARE TWO OF THEM AND THAT IS PERMANENT (decision W2). <see cref="D3D11Framebuffer"/> is an aggregate
    /// over engine textures whose views already exist and never change, and
    /// <see cref="D3D11SwapchainFramebuffer"/> wraps a backbuffer the runtime takes away and hands back on every
    /// resize while its own identity stays stable. They have opposite lifetimes and only
    /// <see cref="IGpuFramebuffer"/> in common, so an emitter that cast to one of them would throw on the other,
    /// which is a crash on the first frame that presents. This seam is what a bind casts to instead.
    /// </para>
    /// <para>
    /// INDEXED RATHER THAN AN ARRAY PROPERTY, deliberately. The swapchain wrapper holds exactly one colour view
    /// in a field and would have to allocate an array per bind to answer one, and the emitter copies the views
    /// into its own scratch array anyway to make the call. One indexer means neither side allocates.
    /// </para>
    /// </summary>
    internal interface ID3D11RenderTargetSurface
    {
        /// <summary>How many colour attachments this framebuffer binds. Zero is legal: a depth-only pass (the
        /// shadow map) binds no render target at all.</summary>
        int RenderTargetCount { get; }

        /// <summary>The colour attachment's <c>ID3D11RenderTargetView</c> at <paramref name="index"/>, never
        /// null: a framebuffer that reported a count has the views to match it.</summary>
        object RenderTargetAt(int index);

        /// <summary>The <c>ID3D11DepthStencilView</c>, or null when this framebuffer has no depth
        /// attachment.</summary>
        object? DepthStencil { get; }
    }
}
