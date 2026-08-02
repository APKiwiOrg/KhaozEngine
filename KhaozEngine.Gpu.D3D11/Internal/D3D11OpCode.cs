namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// The command vocabulary of the native Direct3D 11 backend: one member per <see cref="ID3D11Emitter"/>
    /// call. Every member except <see cref="Begin"/> and <see cref="End"/> is ALSO a stored opcode in
    /// <see cref="D3D11CommandStream"/>, so one enum names both what a recorded op is and what an emitter was
    /// asked to do. That is what lets a trace taken from the deferred driver be compared against a trace taken
    /// from the immediate driver without a translation table in between.
    /// <para>
    /// <see cref="Begin"/> and <see cref="End"/> are scope markers and are never stored. Under the deferred
    /// driver they bracket the REPLAY (<see cref="D3D11StreamReplay"/> raises them around the stored ops), which
    /// is what makes decision R3's "each submit replay opens with exactly one ClearState" a property of replay
    /// rather than of a recorded command. Under the immediate driver they bracket the recording, because that is
    /// when its native calls happen. Either way the emitter sees exactly one pair per submitted list.
    /// </para>
    /// <para>
    /// Ordinals are pinned so a memory dump of a stream reads back the same way on any build, and growth is
    /// naturally by APPEND. That is a convenience rather than a compatibility rule: unlike
    /// <see cref="GpuBackendKind"/>, an opcode is never persisted, never transmitted and never written into a
    /// file name, so nothing outside this process can observe a number here. The spec does not impose an
    /// append-only rule on this enum and this comment does not invent one.
    /// </para>
    /// </summary>
    internal enum D3D11OpCode
    {
        /// <summary>Not a command. Reserved so a zeroed <see cref="D3D11Op"/> is recognisably empty rather than
        /// a valid command, which is what turns a stale or uninitialised slot into a loud replay failure.</summary>
        None = 0,

        /// <summary>Open the emission scope. Never stored (see the type remarks).</summary>
        Begin = 1,
        /// <summary>Close the emission scope. Never stored (see the type remarks).</summary>
        End = 2,

        /// <summary><see cref="IGpuCommandList.SetFramebuffer"/>.</summary>
        SetFramebuffer = 3,
        /// <summary><see cref="IGpuCommandList.ClearColorTarget"/>.</summary>
        ClearColorTarget = 4,
        /// <summary><see cref="IGpuCommandList.ClearDepthStencil"/>.</summary>
        ClearDepthStencil = 5,
        /// <summary><see cref="IGpuCommandList.SetPipeline"/>.</summary>
        SetPipeline = 6,
        /// <summary><see cref="IGpuCommandList.SetGraphicsResourceSet(uint,IGpuResourceSet)"/>, the form with no
        /// dynamic offset. A separate opcode from <see cref="SetGraphicsResourceSetDynamic"/> because the two are
        /// genuinely different binds: a dynamic offset of zero still declares one dynamic element, and a set bound
        /// without an offset declares none.</summary>
        SetGraphicsResourceSet = 7,
        /// <summary><see cref="IGpuCommandList.SetGraphicsResourceSet(uint,IGpuResourceSet,uint)"/>.</summary>
        SetGraphicsResourceSetDynamic = 8,
        /// <summary><see cref="IGpuCommandList.SetVertexBuffer(uint,IGpuBuffer,uint)"/>. The no-offset overload
        /// records this same opcode with an offset of zero, exactly as the incumbent forwards it.</summary>
        SetVertexBuffer = 9,
        /// <summary><see cref="IGpuCommandList.SetIndexBuffer"/>.</summary>
        SetIndexBuffer = 10,
        /// <summary><see cref="IGpuCommandList.SetScissorRect"/>.</summary>
        SetScissorRect = 11,
        /// <summary><see cref="IGpuCommandList.SetFullScissorRects"/>.</summary>
        SetFullScissorRects = 12,
        /// <summary><see cref="IGpuCommandList.Draw(uint,uint,uint,uint)"/>. The single-instance overload records
        /// this same opcode as <c>(vertexCount, 1, 0, 0)</c>.</summary>
        Draw = 13,
        /// <summary><see cref="IGpuCommandList.DrawIndexed"/>.</summary>
        DrawIndexed = 14,
        /// <summary>Either <c>UpdateBuffer</c> overload, erased to bytes in the payload arena.</summary>
        UpdateBuffer = 15,
        /// <summary><see cref="IGpuCommandList.CopyBuffer"/>.</summary>
        CopyBuffer = 16,
        /// <summary><see cref="IGpuCommandList.CopyTexture"/>.</summary>
        CopyTexture = 17,
        /// <summary>Either <c>CopyTextureSubresource</c> overload. The short one records this with a destination
        /// mip and layer of zero, exactly as the incumbent forwards it.</summary>
        CopyTextureSubresource = 18,
        /// <summary><see cref="IGpuCommandList.GenerateMipmaps"/>.</summary>
        GenerateMipmaps = 19,
        /// <summary><see cref="IGpuCommandList.ResolveTexture"/>.</summary>
        ResolveTexture = 20,
        /// <summary><see cref="IGpuCommandList.SetComputePipeline"/>.</summary>
        SetComputePipeline = 21,
        /// <summary><see cref="IGpuCommandList.SetComputeResourceSet(uint,IGpuResourceSet)"/>.</summary>
        SetComputeResourceSet = 22,
        /// <summary><see cref="IGpuCommandList.SetComputeResourceSet(uint,IGpuResourceSet,uint)"/>.</summary>
        SetComputeResourceSetDynamic = 23,
        /// <summary><see cref="IGpuCommandList.Dispatch"/>.</summary>
        Dispatch = 24,
    }
}
