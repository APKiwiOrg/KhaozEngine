using System;
using System.IO;
using System.Text.Json;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfReadReservation(GpuPipelineCompilerReadRequest Request, int MaximumEncodedBytes);

// Renderer-owned byte proof. GPU readers receive only the admitted count/text profile.
internal static class RendererPerfCompilerReadContract
{
    internal static RendererPerfReadReservation? ForAvailable(GpuBackendKind backend, long availableBytes)
    {
        if (backend == GpuBackendKind.VulkanNative)
        {
            const int headerAndExecutables = 262144 + 64 * 8192;
            if (availableBytes < headerAndExecutables) return null;
            int total = checked((int)Math.Min(16384, (availableBytes - headerAndExecutables) / 4096));
            return new(new(64, 256, total, 256, 16384, 4), checked(headerAndExecutables + total * 4096));
        }
        if (backend == GpuBackendKind.Direct3D11Native)
            return availableBytes < 1048576 ? null : new(new(2, 30, 60, 4096, 16384, 4), 1048576);
        throw new ArgumentException("Only primary Vulkan and D3D11 read profiles are admitted.", nameof(backend));
    }

    internal static int EncodedResultBytes(GpuPipelineCompilerReadResult result)
    {
        var q = result.Request;
        int maximum = q == new GpuPipelineCompilerReadRequest(2, 30, 60, 4096, 16384, 4) ? 1048576
            : q.MaximumExecutables == 64 && q.MaximumStatisticsPerExecutable == 256 && q.MaximumTextUtf8Bytes == 256
                && q.MaximumCompilerFlagsUtf8Bytes == 16384 && q.MaximumEnumerationAttempts == 4 && q.MaximumTotalStatistics is >= 0 and <= 16384
                ? checked(262144 + 64 * 8192 + q.MaximumTotalStatistics * 4096)
                : throw new ArgumentException("Unexpected compiler-read profile.", nameof(result));
        using var stream = new CountStream(maximum);
        JsonSerializer.Serialize(stream, result.Report, RendererPerfManifest.JsonOptions);
        JsonSerializer.Serialize(stream, new RendererPerfCompilerReadEvidence(q, result.Counts, result.Complete, result.RefusalReason), RendererPerfManifest.JsonOptions);
        // Surrounding array commas/property delimiters and the result's fixed association syntax.
        stream.Write(new byte[256]);
        return checked((int)stream.Length);
    }

    sealed class CountStream(int maximum) : Stream
    {
        long _length;
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            long next = checked(_length + buffer.Length);
            if (next > maximum) throw new InvalidOperationException($"Compiler encoding requires {next} bytes, above reservation {maximum}.");
            _length = next;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
