using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE REPLAY LOOP: decode each recorded <see cref="D3D11Op"/> and issue it into an emitter, in record order.
    /// The exact inverse of <see cref="D3D11StreamEmitter"/>, and kept in its own type so the encoder, the
    /// storage and this switch never grow into one file.
    /// <para>
    /// Generic over a STRUCT emitter, so the JIT compiles one specialised copy of this switch per emitter type
    /// and every call below is a direct call rather than an interface dispatch. That is the property section 5.1
    /// asks for, and it is why the emitter is a constrained type parameter everywhere instead of an interface
    /// reference.
    /// </para>
    /// <para>
    /// The pair of scope calls around the loop is decision R3: EXACTLY ONE per replay, which is where a real
    /// emitter issues its single <c>ClearState</c>. They are raised here rather than recorded as ops precisely so
    /// that replaying the same stream twice opens two clean scopes, and so that <c>Begin</c> on the recording
    /// side stays a truncation with no device contact.
    /// </para>
    /// </summary>
    internal static class D3D11StreamReplay
    {
        /// <summary>Replay <paramref name="stream"/> into <paramref name="emitter"/>.</summary>
        internal static void Run<TEmitter>(D3D11CommandStream stream, ref TEmitter emitter)
            where TEmitter : struct, ID3D11Emitter
        {
            if (stream is null) throw new ArgumentNullException(nameof(stream));

            emitter.Begin();
            ReadOnlySpan<D3D11Op> ops = stream.Ops;
            for (int i = 0; i < ops.Length; i++) Invoke(stream, in ops[i], ref emitter);
            emitter.End();
        }

        static void Invoke<TEmitter>(D3D11CommandStream stream, in D3D11Op op, ref TEmitter emitter)
            where TEmitter : struct, ID3D11Emitter
        {
            switch (op.Code)
            {
                case D3D11OpCode.SetFramebuffer:
                    emitter.SetFramebuffer(stream.Reference<IGpuFramebuffer>(op.Reference));
                    break;

                case D3D11OpCode.ClearColorTarget:
                    emitter.ClearColorTarget(op.Arg0, new Color(
                        D3D11Op.Float(op.Arg1), D3D11Op.Float(op.Arg2), D3D11Op.Float(op.Arg3), D3D11Op.Float(op.Arg4)));
                    break;

                case D3D11OpCode.ClearDepthStencil:
                    emitter.ClearDepthStencil(D3D11Op.Float(op.Arg0));
                    break;

                case D3D11OpCode.SetPipeline:
                    emitter.SetPipeline(stream.Reference<IGpuPipeline>(op.Reference));
                    break;

                case D3D11OpCode.SetGraphicsResourceSet:
                    emitter.SetGraphicsResourceSet(op.Arg0, stream.Reference<IGpuResourceSet>(op.Reference));
                    break;

                case D3D11OpCode.SetGraphicsResourceSetDynamic:
                    emitter.SetGraphicsResourceSet(op.Arg0, stream.Reference<IGpuResourceSet>(op.Reference), op.Arg1);
                    break;

                case D3D11OpCode.SetVertexBuffer:
                    emitter.SetVertexBuffer(op.Arg0, stream.Reference<IGpuBuffer>(op.Reference), op.Arg1);
                    break;

                case D3D11OpCode.SetIndexBuffer:
                    emitter.SetIndexBuffer(stream.Reference<IGpuBuffer>(op.Reference), (GpuIndexFormat)op.Arg0);
                    break;

                case D3D11OpCode.SetScissorRect:
                    emitter.SetScissorRect(op.Arg0, op.Arg1, op.Arg2, op.Arg3, op.Arg4);
                    break;

                case D3D11OpCode.SetFullScissorRects:
                    emitter.SetFullScissorRects();
                    break;

                case D3D11OpCode.Draw:
                    emitter.Draw(op.Arg0, op.Arg1, op.Arg2, op.Arg3);
                    break;

                case D3D11OpCode.DrawIndexed:
                    emitter.DrawIndexed(op.Arg0, op.Arg1, op.Arg2, D3D11Op.Signed(op.Arg3), op.Arg4);
                    break;

                case D3D11OpCode.UpdateBuffer:
                    emitter.UpdateBuffer(stream.Reference<IGpuBuffer>(op.Reference), op.Arg0,
                        stream.Payload(D3D11Op.Signed(op.Arg1), D3D11Op.Signed(op.Arg2)));
                    break;

                case D3D11OpCode.CopyBuffer:
                    emitter.CopyBuffer(
                        stream.Reference<IGpuBuffer>(op.Reference), op.Arg1,
                        stream.Reference<IGpuBuffer>(D3D11Op.Signed(op.Arg0)), op.Arg2, op.Arg3);
                    break;

                case D3D11OpCode.CopyTexture:
                    emitter.CopyTexture(
                        stream.Reference<IGpuTexture>(op.Reference),
                        stream.Reference<IGpuTexture>(D3D11Op.Signed(op.Arg0)));
                    break;

                case D3D11OpCode.CopyTextureSubresource:
                    emitter.CopyTextureSubresource(
                        stream.Reference<IGpuTexture>(op.Reference),
                        D3D11Op.MipOf(op.Arg1), D3D11Op.LayerOf(op.Arg1),
                        stream.Reference<IGpuTexture>(D3D11Op.Signed(op.Arg0)),
                        D3D11Op.MipOf(op.Arg2), D3D11Op.LayerOf(op.Arg2),
                        op.Arg3, op.Arg4);
                    break;

                case D3D11OpCode.GenerateMipmaps:
                    emitter.GenerateMipmaps(stream.Reference<IGpuTexture>(op.Reference));
                    break;

                case D3D11OpCode.ResolveTexture:
                    emitter.ResolveTexture(
                        stream.Reference<IGpuTexture>(op.Reference),
                        stream.Reference<IGpuTexture>(D3D11Op.Signed(op.Arg0)));
                    break;

                case D3D11OpCode.SetComputePipeline:
                    emitter.SetComputePipeline(stream.Reference<IGpuComputePipeline>(op.Reference));
                    break;

                case D3D11OpCode.SetComputeResourceSet:
                    emitter.SetComputeResourceSet(op.Arg0, stream.Reference<IGpuResourceSet>(op.Reference));
                    break;

                case D3D11OpCode.SetComputeResourceSetDynamic:
                    emitter.SetComputeResourceSet(op.Arg0, stream.Reference<IGpuResourceSet>(op.Reference), op.Arg1);
                    break;

                case D3D11OpCode.Dispatch:
                    emitter.Dispatch(op.Arg0, op.Arg1, op.Arg2);
                    break;

                // Begin and End are scope markers raised by Run around the loop, so meeting one as a stored op
                // means the encoder recorded something it must not. None means a slot was never written. Both are
                // defects in this package, so both are loud rather than skipped: a silently ignored op replays a
                // frame that is missing a command, which reads as a rendering bug somewhere else entirely.
                default:
                    throw new InvalidOperationException(
                        $"The Direct3D 11 command stream holds an op the replay cannot issue: {op.Code}. "
                        + "Begin and End are raised around the replay rather than recorded, and None is an "
                        + "unwritten slot, so this is a defect in the op encoder.");
            }
        }
    }
}
