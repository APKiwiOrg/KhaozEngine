using System;
using System.Globalization;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// GPU-to-CPU readback. <see cref="ToRgba"/> / <see cref="ToRgbaMip"/> copy a rendered texture back to a
    /// tightly-packed CPU RGBA8 buffer (<c>width * height * 4</c> bytes, row-major, top-left origin): they
    /// allocate a staging texture, blit into it, map it, and de-stride the driver's
    /// <see cref="MappedData.RowPitch"/> into packed rows. Shared by the Render2D and Render3D headless snapshot
    /// helpers (their readback used to be duplicated verbatim). <see cref="ReadBuffer{T}"/> is the buffer
    /// equivalent, for reading a compute-written storage buffer back as a typed array. The source resource must
    /// be done being written before any of these is called (each submits its own copy and drains, but the work
    /// that PRODUCED the data has to have been submitted first).
    /// <para>
    /// NONE OF THESE MAY BE CALLED WHILE A FRAME IS RECORDING. Each one opens, submits and drains a command list
    /// of its own, which is the second recording the seam's one-open-recording-per-device contract forbids (see
    /// <see cref="IGpuCommandList.Begin"/>). A readback is a synchronous, whole-pipeline operation anyway, so the
    /// place for it is between frames rather than inside one. Called from inside a recording the engine opened,
    /// it refuses with a <see cref="GpuNestedRecordingException"/> naming both sides instead of corrupting the
    /// device (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/424">#424</see>).
    /// </para>
    /// <para>
    /// <see cref="ToRgba"/> takes a whole-texture copy, so its source must ALREADY be single-mip, single-sample
    /// <see cref="GpuPixelFormat.R8G8B8A8UNorm"/> at the size asked for, and one that is not is refused here by
    /// name rather than inside a backend's copy
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/83">#83</see>).
    /// </para>
    /// </summary>
    public static class GpuReadback
    {
        /// <summary>Read <paramref name="src"/> back as packed RGBA8. Requires a mappable device (a Metal GPU here).
        /// <para><paramref name="src"/> MUST ALREADY BE a <see cref="GpuPixelFormat.R8G8B8A8UNorm"/>, single-mip,
        /// single-sample texture of exactly <paramref name="width"/> x <paramref name="height"/>: the copy is a
        /// whole-texture one into a staging texture of that shape. Anything else is refused with an
        /// <see cref="ArgumentException"/> naming what the source actually is (see
        /// <see cref="RequireCopyableSource"/>). Read one level of a mipped texture with
        /// <see cref="ToRgbaMip"/>.</para></summary>
        public static byte[] ToRgba(IGpuDevice gd, IGpuTexture src, int width, int height)
        {
            RequireCopyableSource(src, width, height);
            var f = gd.Factory;
            using IGpuTexture staging = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)width, (uint)height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                using (GpuRecording.Open(gd, cl, "GpuReadback.ToRgba")) cl.CopyTexture(src, staging);
                gd.Submit(cl);
                gd.WaitForIdle();
            }

            var outBytes = new byte[width * height * 4];
            MappedData map = gd.Map(staging, GpuMapMode.Read);
            unsafe
            {
                byte* data = (byte*)map.Data;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        uint si = (uint)(y * (int)map.RowPitch + x * 4);
                        int di = (y * width + x) * 4;
                        outBytes[di + 0] = data[si + 0];
                        outBytes[di + 1] = data[si + 1];
                        outBytes[di + 2] = data[si + 2];
                        outBytes[di + 3] = data[si + 3];
                    }
            }
            gd.Unmap(staging);
            return outBytes;
        }

        /// <summary>
        /// THE SHAPE <see cref="ToRgba"/> ASSUMES OF ITS SOURCE, CHECKED AT THE READBACK RATHER THAN INSIDE A
        /// BACKEND'S COPY (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/83">#83</see>). That method
        /// allocates a single-mip, single-sample <see cref="GpuPixelFormat.R8G8B8A8UNorm"/> staging texture of the
        /// size it was asked for and whole-texture copies into it, then de-strides at a fixed four bytes per texel.
        /// A whole copy names every subresource on both sides, so every one of those has to already match.
        ///
        /// <para><b>WHY HERE AND NOT ONLY IN THE BACKENDS.</b> Two of the three refuse a mismatched whole copy
        /// themselves (<c>RequireMatchingShape</c> in the native Metal and Vulkan command lists), but Direct3D 11's
        /// <c>CopyResource</c> is silent about it, so the same call threw on two backends and read back
        /// channel-swapped or garbage bytes on the third. Refusing at the call site is what makes the three agree,
        /// and the message names the READBACK and the source's actual format and mip count rather than a copy the
        /// caller never wrote.</para>
        ///
        /// <para>Device-free by construction: everything it reads is on the handle, so it runs before anything is
        /// allocated, recorded or submitted.</para>
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="src"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="src"/> is not a single-mip, single-sample
        /// <see cref="GpuPixelFormat.R8G8B8A8UNorm"/> texture of exactly <paramref name="width"/> x
        /// <paramref name="height"/>.</exception>
        static void RequireCopyableSource(IGpuTexture src, int width, int height)
        {
            ArgumentNullException.ThrowIfNull(src);
            if (src.Format == GpuPixelFormat.R8G8B8A8UNorm && src.MipLevels == 1 && src.SampleCount == 1
                && width >= 0 && height >= 0 && src.Width == (uint)width && src.Height == (uint)height)
            {
                return;
            }

            throw new ArgumentException(
                "An RGBA8 readback (GpuReadback.ToRgba) was asked for "
                + width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture)
                + " of a texture that is " + Describe(src)
                + ". The readback copies the WHOLE source into a staging texture that is "
                + width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture)
                + " R8G8B8A8UNorm with 1 mip level and 1 sample, and a whole-texture copy names every subresource "
                + "on both sides, so the source has to be exactly that. Native Metal and Vulkan refuse the copy "
                + "themselves, Direct3D 11's CopyResource does not, so an unchecked mismatch is bytes in the wrong "
                + "order on one backend and an exception on the other two. Resolve a multisampled target "
                + "(IGpuCommandList.ResolveTexture) or convert a B8G8R8A8UNorm one before reading it back, read one "
                + "level of a mip chain with GpuReadback.ToRgbaMip, and pass the source's own dimensions.",
                nameof(src));
        }

        // What the source actually is, for the refusal above. Every field the whole copy compares, in one phrase.
        static string Describe(IGpuTexture t) =>
            t.Width.ToString(CultureInfo.InvariantCulture) + "x" + t.Height.ToString(CultureInfo.InvariantCulture)
            + " " + t.Format + " with " + t.MipLevels.ToString(CultureInfo.InvariantCulture) + " mip level(s) and "
            + t.SampleCount.ToString(CultureInfo.InvariantCulture) + " sample(s)";

        /// <summary>Read one mip level + array layer of <paramref name="src"/> back as packed RGBA8
        /// (<paramref name="mipWidth"/> x <paramref name="mipHeight"/>, the mip's own dimensions). Copies that
        /// subresource into a staging texture, maps it, and de-strides the rows. Useful for verifying a generated
        /// mip chain (e.g. that a high mip is a real blurred downsample, not a copy of mip 0 or empty). Requires a
        /// mappable device.</summary>
        public static byte[] ToRgbaMip(IGpuDevice gd, IGpuTexture src, uint mipLevel, uint arrayLayer, int mipWidth, int mipHeight)
        {
            var f = gd.Factory;
            using IGpuTexture staging = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)mipWidth, (uint)mipHeight, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Staging));
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                using (GpuRecording.Open(gd, cl, "GpuReadback.ToRgbaMip"))
                    cl.CopyTextureSubresource(src, mipLevel, arrayLayer, staging, (uint)mipWidth, (uint)mipHeight);
                gd.Submit(cl);
                gd.WaitForIdle();
            }

            var outBytes = new byte[mipWidth * mipHeight * 4];
            MappedData map = gd.Map(staging, GpuMapMode.Read);
            unsafe
            {
                byte* data = (byte*)map.Data;
                for (int y = 0; y < mipHeight; y++)
                    for (int x = 0; x < mipWidth; x++)
                    {
                        uint si = (uint)(y * (int)map.RowPitch + x * 4);
                        int di = (y * mipWidth + x) * 4;
                        outBytes[di + 0] = data[si + 0];
                        outBytes[di + 1] = data[si + 1];
                        outBytes[di + 2] = data[si + 2];
                        outBytes[di + 3] = data[si + 3];
                    }
            }
            gd.Unmap(staging);
            return outBytes;
        }

        /// <summary>Read <paramref name="elementCount"/> elements of <typeparamref name="T"/> back from
        /// <paramref name="src"/> (typically a <see cref="GpuBufferUsage.StructuredBufferReadWrite"/> buffer a
        /// compute pass just wrote), starting at <paramref name="srcOffsetBytes"/>. Allocates a
        /// <see cref="GpuBufferUsage.Staging"/> buffer, copies into it, drains, maps, and copies out - the buffer
        /// counterpart of <see cref="ToRgba"/>, so a compute consumer does not re-derive the sequence.
        /// <typeparamref name="T"/>'s layout must match the shader's, which for a std430 buffer means watching the
        /// usual scalar/vec3 padding rules (a <c>vec3</c> member occupies 16 bytes).
        /// <para>
        /// <b><paramref name="srcOffsetBytes"/> MUST BE A MULTIPLE OF FOUR</b>, which is what
        /// <see cref="IGpuCommandList.CopyBuffer"/> requires of an offset on every backend, and one that is not
        /// is refused here with an <see cref="ArgumentOutOfRangeException"/> before anything is allocated,
        /// recorded or submitted. Until 17.40.0 this offset went into the copy unfiltered, so the same call
        /// succeeded on three backends and threw on native Metal, whose copy selector requires the alignment on
        /// macOS (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/602">#602</see>). Refusing rather
        /// than rounding is the half of that choice worth stating: an offset selects WHICH bytes come back, so
        /// rounding it up would quietly hand the caller a different slice than the one they asked for. An
        /// unaligned start is legal to READ, just not to copy from, so map the buffer or reach it through the
        /// device-level API instead.
        /// </para>
        /// </summary>
        public static T[] ReadBuffer<T>(IGpuDevice gd, IGpuBuffer src, int elementCount, uint srcOffsetBytes = 0)
            where T : unmanaged
        {
            if (elementCount < 0) throw new ArgumentOutOfRangeException(nameof(elementCount));
            GpuCopyAlignment.RequireAlignedOffset(srcOffsetBytes, nameof(srcOffsetBytes),
                "A buffer readback (GpuReadback.ReadBuffer)", "source");
            var result = new T[elementCount];
            if (elementCount == 0) return result;

            uint sizeBytes;
            unsafe { sizeBytes = (uint)(elementCount * sizeof(T)); }

            var f = gd.Factory;
            using IGpuBuffer staging = f.CreateBuffer(new GpuBufferDescription(sizeBytes, GpuBufferUsage.Staging));
            // Drain BEFORE the copy, not only after it. The compute work that produced the data was submitted on
            // another command list, and a copy in a later submission is not ordered against it on every backend
            // (the Veldrid Vulkan leg, deleted in 18.0.0, carried no semaphores on its submissions and emitted no
            // barrier ahead of its buffer copy). A readback is a synchronous operation anyway, so an extra drain
            // on an already-idle device
            // costs nothing and removes the footgun.
            gd.WaitForIdle();
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                using (GpuRecording.Open(gd, cl, "GpuReadback.ReadBuffer"))
                    cl.CopyBuffer(src, srcOffsetBytes, staging, 0, sizeBytes);
                gd.Submit(cl);
                gd.WaitForIdle();
            }

            MappedData map = gd.Map(staging, GpuMapMode.Read);
            unsafe
            {
                var span = new ReadOnlySpan<T>((void*)map.Data, elementCount);
                span.CopyTo(result);
            }
            gd.Unmap(staging);
            return result;
        }
    }
}
