using System;
using KhaozEngine.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SAME <c>CopyBuffer</c> OFFSET CONTRACT, ON WHATEVER REAL DEVICE THE HOST RESOLVES
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/684">#684</see>). The device-free companion
    /// (<see cref="CopyBufferOffsetContractTests"/>) drives all four implementations side by side and can assert
    /// that they agree. What it cannot do is copy a byte, so this is where the ACCEPT side is worth anything: an
    /// aligned copy at a non-zero offset moves the bytes the caller asked for, and the refusal is a refusal on a
    /// real driver rather than only in a fake.
    ///
    /// <para><b>ONE TEST, RUN FIVE TIMES BY THE MATRIX, WHICH IS THE POINT.</b> There is no backend named
    /// anywhere here: the test asks for a headless device and asserts the contract against whatever came back.
    /// So the Metal leg runs it on Metal, the two Windows legs on Direct3D 11 (incumbent and native) and the two
    /// Linux legs on Vulkan (incumbent and native), and the claim that all four backends agree is checked on
    /// four real drivers instead of being inferred from four fakes. The backend that answered is written to the
    /// test output, because "it passed" means very little without knowing which one it passed on.</para>
    ///
    /// <para><b>OFFSET FOUR IS THE VALUE ON PURPOSE.</b> It is the smallest legal non-zero offset, so it fails if
    /// a backend ever hardens the rule to something wider, and being non-zero is what makes the readback prove
    /// the offset was honoured rather than ignored: the expected slice is different from the buffer's
    /// start.</para>
    /// </summary>
    public sealed class CopyBufferOffsetGpuTests
    {
        readonly ITestOutputHelper _output;

        public CopyBufferOffsetGpuTests(ITestOutputHelper output) => _output = output;

        const uint Elements = 8;
        const uint ElementBytes = sizeof(uint);
        const uint AlignedOffset = 4;             // one element in, and the smallest offset the seam accepts
        const uint UnalignedOffset = 3;

        [GpuFact]
        public void AnAlignedOffsetCopiesTheSliceTheCallerAskedFor_AndAnUnalignedOneIsRefused()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            _output.WriteLine("backend: " + dev.Backend + " (" + dev.Capabilities.DeviceName + ")");

            IGpuResourceFactory f = dev.Factory;
            using IGpuBuffer source = f.CreateBuffer(new GpuBufferDescription(
                Elements * ElementBytes, GpuBufferUsage.StructuredBufferReadWrite, ElementBytes));
            using IGpuBuffer staging = f.CreateBuffer(new GpuBufferDescription(
                Elements * ElementBytes, GpuBufferUsage.Staging));

            var values = new uint[Elements];
            for (uint i = 0; i < Elements; i++) values[i] = 0xA000_0000u + i;
            dev.UpdateBuffer(source, 0, values);
            dev.WaitForIdle();

            // ---- The seam's own copy, at an offset of one element -------------------------------------------
            const uint copied = (Elements - 1) * ElementBytes;
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                using (GpuRecording.Open(dev, cl, nameof(CopyBufferOffsetGpuTests)))
                    cl.CopyBuffer(source, AlignedOffset, staging, 0, copied);
                dev.Submit(cl);
                dev.WaitForIdle();
            }

            uint[] read = ReadStaging(dev, staging, (int)(Elements - 1));
            for (int i = 0; i < read.Length; i++)
            {
                Assert.Equal(values[i + 1], read[i]);
            }

            // ---- And the refusal, on the same device, through the same member ------------------------------
            ArgumentOutOfRangeException seam = Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                using IGpuCommandList cl = f.CreateCommandList();
                using (GpuRecording.Open(dev, cl, nameof(CopyBufferOffsetGpuTests)))
                    cl.CopyBuffer(source, UnalignedOffset, staging, 0, copied);
            });

            Assert.Equal("srcOffsetBytes", seam.ParamName);
            Assert.Contains("not a multiple of 4", seam.Message, StringComparison.Ordinal);

            // ---- And through the helper that produced the divergence in the first place --------------------
            uint[] viaHelper = GpuReadback.ReadBuffer<uint>(dev, source, (int)(Elements - 1), AlignedOffset);
            for (int i = 0; i < viaHelper.Length; i++)
            {
                Assert.Equal(values[i + 1], viaHelper[i]);
            }

            ArgumentOutOfRangeException helper = Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuReadback.ReadBuffer<uint>(dev, source, (int)(Elements - 1), UnalignedOffset));

            Assert.Equal("srcOffsetBytes", helper.ParamName);
            Assert.Contains("A buffer readback (GpuReadback.ReadBuffer)", helper.Message, StringComparison.Ordinal);
        }

        /// <summary>Map the staging buffer and copy <paramref name="count"/> unsigned integers out of it. The
        /// copy above is the subject of the test, so the readback deliberately does NOT go through
        /// <see cref="GpuReadback.ReadBuffer{T}"/>: that helper would issue a second copy of its own and the
        /// assertion would no longer be about the first one.</summary>
        static uint[] ReadStaging(IGpuDevice dev, IGpuBuffer staging, int count)
        {
            var read = new uint[count];
            MappedData map = dev.Map(staging, GpuMapMode.Read);
            unsafe
            {
                new ReadOnlySpan<uint>((void*)map.Data, count).CopyTo(read);
            }

            dev.Unmap(staging);
            return read;
        }
    }
}
