using System;
using System.Globalization;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// AN EMITTER WITH NO DEVICE BEHIND IT: every call is counted and traced into a
    /// <see cref="D3D11EmitterCallLog"/> and nothing else happens. This is the vehicle for the op-encoding and
    /// replay-ordering tests, and it works because <see cref="ID3D11Emitter"/> is written in engine-owned handle
    /// types: there is no COM pointer anywhere in the seam that would need a real device to stand in for.
    /// <para>
    /// Section 2.1 settles what this does and does not prove. A counting emitter is a plain object under EITHER
    /// recording model, so it is not an argument for the deferred one. What it is, is the reason those tests are
    /// a device-free <c>[Fact]</c> that runs under a plain <c>dotnet test</c> on macOS and Linux rather than a
    /// <c>[GpuFact]</c> gated on a Windows machine with a D3D11 device.
    /// </para>
    /// <para>
    /// WHAT IT COUNTS IS SEAM CALLS, AND DECISION T2 GATES NATIVE CALLS. The two are not the same number and
    /// this emitter cannot see the difference: a resource-set bind here is up to six native calls inside the
    /// real emitter (5.3), a redundant pipeline bind there is zero, and 9.4's one <c>RSSetViewports</c> plus one
    /// <c>RSSetScissorRects</c> per framebuffer CHANGE with zero for a re-bind turns on a guard that lives in
    /// the real emitter. So what this measures is an upper-bound input and an ordering check, and calling it the
    /// budget test would freeze a number the budget is not made of.
    /// </para>
    /// <para>
    /// THE SINK DECISION, TAKEN IN ROW 9, and it is not either of the two shapes this comment used to weigh. The
    /// old framing was a countable sink BELOW the real emitter (no drift, at the cost of a second generic
    /// parameter on the emitter) against a device-free harness that reproduces the fan-out and the guards (cheap,
    /// and structurally able to drift). What settled it is that the replay row had already moved every redundancy
    /// and viewport guard into the device-owned <see cref="D3D11DeviceState"/>, so a third shape was available:
    /// put the SCHEDULE and the FAN-OUT in device-owned, device-free types beside those guards
    /// (<see cref="D3D11BindFlush"/> and <see cref="D3D11SetActivation"/>), have them decide which calls to make
    /// and hand them to an <see cref="ID3D11BindSink"/>, and let the real emitter and
    /// <see cref="D3D11NativeTraceEmitter"/> supply the two ends of that one seam.
    /// </para>
    /// <para>
    /// WHAT THAT BUYS is that the budget is taken over the SHIPPED dirty tracking, the shipped slot order, the
    /// shipped pipeline-switch drain, the shipped register arithmetic and the shipped batching, with no second
    /// implementation of any of them, and it costs no generic parameter on the emitter. What can still drift is
    /// the naming translation alone, which is <see cref="D3D11NativeCallName"/> deciding that the vertex stage
    /// plus the <c>b</c> file is <c>VSSetConstantBuffers1</c>, and both emitters go through that same function.
    /// So decision T3's WARP <c>[GpuFact]</c> is a belt-and-braces check on one mapping rather than the only
    /// thing standing between the budget and reality, and the budget has a meaningful gate before row 17 lands
    /// it.
    /// </para>
    /// <para>
    /// It also drives BOTH drivers unchanged, which is the sharpest test available here: record the same seam
    /// calls through the deferred recorder and through the immediate one, and the two traces must be identical.
    /// The immediate driver has no stream at all, so an identical trace is the seam property section 16 requires,
    /// namely that the op stream is one driver of this emitter and not a layer under it.
    /// </para>
    /// <para>
    /// A readonly struct over one class reference, so the JIT monomorphizes it like any other emitter and a copy
    /// of it still writes to the same log.
    /// </para>
    /// </summary>
    internal readonly struct D3D11CountingEmitter : ID3D11Emitter
    {
        readonly D3D11EmitterCallLog _log;

        internal D3D11CountingEmitter(D3D11EmitterCallLog log)
            => _log = log ?? throw new ArgumentNullException(nameof(log));

        /// <summary>Where the counts and the trace land.</summary>
        internal D3D11EmitterCallLog Log => _log;

        public void Begin() => _log.Record(D3D11OpCode.Begin);

        public void End() => _log.Record(D3D11OpCode.End);

        public void SetFramebuffer(IGpuFramebuffer framebuffer)
            => _log.Record(D3D11OpCode.SetFramebuffer, _log.Id(framebuffer));

        public void ClearColorTarget(uint index, Color rgba)
            => _log.Record(D3D11OpCode.ClearColorTarget,
                $"{N(index)},{N(rgba.R)},{N(rgba.G)},{N(rgba.B)},{N(rgba.A)}");

        public void ClearDepthStencil(float depth)
            => _log.Record(D3D11OpCode.ClearDepthStencil, N(depth));

        public void SetPipeline(IGpuPipeline pipeline)
            => _log.Record(D3D11OpCode.SetPipeline, _log.Id(pipeline));

        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
            => _log.Record(D3D11OpCode.SetGraphicsResourceSet, $"{N(slot)},{_log.Id(set)}");

        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => _log.Record(D3D11OpCode.SetGraphicsResourceSetDynamic,
                $"{N(slot)},{_log.Id(set)},{N(dynamicOffset)}");

        public void SetVertexBuffer(uint slot, IGpuBuffer buffer, uint offsetBytes)
            => _log.Record(D3D11OpCode.SetVertexBuffer, $"{N(slot)},{_log.Id(buffer)},{N(offsetBytes)}");

        public void SetIndexBuffer(IGpuBuffer buffer, GpuIndexFormat format)
            => _log.Record(D3D11OpCode.SetIndexBuffer, $"{_log.Id(buffer)},{format}");

        public void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
            => _log.Record(D3D11OpCode.SetScissorRect, $"{N(index)},{N(x)},{N(y)},{N(width)},{N(height)}");

        public void SetFullScissorRects() => _log.Record(D3D11OpCode.SetFullScissorRects);

        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
            => _log.Record(D3D11OpCode.Draw,
                $"{N(vertexCount)},{N(instanceCount)},{N(vertexStart)},{N(instanceStart)}");

        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
            => _log.Record(D3D11OpCode.DrawIndexed,
                $"{N(indexCount)},{N(instanceCount)},{N(indexStart)},{N(vertexOffset)},{N(instanceStart)}");

        /// <summary>The bytes are summarised by length and by a checksum rather than dumped. A trace exists to be
        /// compared and read, and a several-kilobyte vertex payload inline would defeat both, while a length that
        /// matches over content that does not is exactly the encoding bug worth catching.</summary>
        public void UpdateBuffer(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<byte> data)
            => _log.Record(D3D11OpCode.UpdateBuffer,
                $"{_log.Id(buffer)},{N(offsetBytes)},{N(data.Length)}b,{N(Checksum(data))}");

        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes)
            => _log.Record(D3D11OpCode.CopyBuffer,
                $"{_log.Id(src)},{N(srcOffsetBytes)},{_log.Id(dst)},{N(dstOffsetBytes)},{N(sizeInBytes)}");

        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
            => _log.Record(D3D11OpCode.CopyTexture, $"{_log.Id(src)},{_log.Id(dst)}");

        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
            IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
            => _log.Record(D3D11OpCode.CopyTextureSubresource,
                $"{_log.Id(src)},{N(srcMipLevel)},{N(srcArrayLayer)},{_log.Id(dst)},{N(dstMipLevel)},"
                + $"{N(dstArrayLayer)},{N(width)},{N(height)}");

        public void GenerateMipmaps(IGpuTexture texture)
            => _log.Record(D3D11OpCode.GenerateMipmaps, _log.Id(texture));

        public void ResolveTexture(IGpuTexture src, IGpuTexture dst)
            => _log.Record(D3D11OpCode.ResolveTexture, $"{_log.Id(src)},{_log.Id(dst)}");

        public void SetComputePipeline(IGpuComputePipeline pipeline)
            => _log.Record(D3D11OpCode.SetComputePipeline, _log.Id(pipeline));

        public void SetComputeResourceSet(uint slot, IGpuResourceSet set)
            => _log.Record(D3D11OpCode.SetComputeResourceSet, $"{N(slot)},{_log.Id(set)}");

        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => _log.Record(D3D11OpCode.SetComputeResourceSetDynamic,
                $"{N(slot)},{_log.Id(set)},{N(dynamicOffset)}");

        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
            => _log.Record(D3D11OpCode.Dispatch, $"{N(groupCountX)},{N(groupCountY)},{N(groupCountZ)}");

        // Invariant culture throughout, so a trace compares equal on a machine whose decimal separator is a
        // comma. A trace that differs by locale would fail the two-driver comparison for a reason that has
        // nothing to do with either driver.
        static string N(uint value) => value.ToString(CultureInfo.InvariantCulture);
        static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
        static string N(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        // FNV-1a, chosen for being short enough to read inline and stable across runs. It is a content check for
        // a test trace and nothing else claims to be.
        static uint Checksum(ReadOnlySpan<byte> data)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= 16777619u;
            }

            return hash;
        }
    }
}
