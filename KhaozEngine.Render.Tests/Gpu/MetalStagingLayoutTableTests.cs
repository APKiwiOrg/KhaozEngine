using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE CHECKED-IN STAGING LAYOUT TABLE, and the assertion of <see cref="MetalStagingLayout"/> against it.
    /// Decision M-C5, section 13, work-breakdown row 6.
    ///
    /// <para><b>WHY A TABLE AND NOT A RE-DERIVATION.</b> Every golden in the suite reads back through
    /// <c>IGpuDevice.Map(staging, ...)</c> and consumes <see cref="MappedData.RowPitch"/>, so an arithmetic that
    /// disagrees with the incumbent's garbles all 36 at once, silently: the readback succeeds, the pointer is
    /// valid, and the pixels are in the wrong places. A test that re-derived the numbers from the same reasoning
    /// the implementation used would pass on a shared mistake, which is the failure phase 2's own header records
    /// for a self-baked golden. So the numbers below are LITERALS, produced by transcribing the incumbent's nine
    /// functions into a throwaway generator and running it, and this file is what makes "should be identical" a
    /// checked fact BEFORE a single golden runs.</para>
    ///
    /// <para><b>WHERE THE NUMBERS CAME FROM, so they can be regenerated rather than trusted.</b> Veldrid
    /// <c>4.9.103</c>, the version <c>Directory.Packages.props</c> pins:
    /// <c>FormatSizeHelpers.GetSizeInBytes</c>, <c>FormatHelpers.GetRowPitch</c>, <c>GetNumRows</c>,
    /// <c>GetDepthPitch</c>, <c>GetRegionSize</c>, <c>Util.GetDimension</c>, <c>Util.ComputeMipOffset</c>,
    /// <c>Util.ComputeArrayLayerOffset</c> and <c>Util.ComputeSubresourceOffset</c>, composed exactly as
    /// <c>MTLTexture</c>'s staging constructor branch, <c>MTLTexture.GetSubresourceLayout</c> and
    /// <c>MTLTexture.GetSubresourceSize</c> compose them. Depth is 1 in every row because
    /// <see cref="GpuTextureDescription"/> has no depth parameter, and no row is block-compressed because
    /// <see cref="GpuPixelFormat"/> has no compressed member.</para>
    ///
    /// <para><b>THE SPREAD IS CHOSEN RATHER THAN CONVENIENT.</b> All eight seam formats, including the five-byte
    /// <see cref="GpuPixelFormat.D32FloatS8UInt"/> that is the one value a reader doubts. A 1x1 texture, an odd
    /// non-power-of-two (17x5), a rectangular power-of-two (256x128), a full mip chain that reaches 1x1
    /// (64x64 at 7 levels), a non-power-of-two chain whose halving truncates (100x60 at 3 levels), a layered
    /// texture with no mips (64x64 at 4 layers), and one that is both at once and non-power-of-two (33x17 at 4
    /// levels and 3 layers), which is where a mip offset and a layer pitch can disagree.</para>
    ///
    /// <para><b>DEVICE-FREE, so it runs on every <c>dotnet test</c> on every leg</b>, which is the point: the
    /// arithmetic is software on both sides, so agreeing with the incumbent is a statement about code rather than
    /// about a driver, and it is checkable on the Linux and Windows legs where there is no Metal at all.</para>
    /// </summary>
    public sealed class MetalStagingLayoutTableTests
    {
        readonly ITestOutputHelper _output;

        public MetalStagingLayoutTableTests(ITestOutputHelper output) => _output = output;

        /// <summary>One expected row: the shape, the whole buffer's size, and one subresource's layout.</summary>
        /// <param name="Format">The seam pixel format.</param>
        /// <param name="Width">Mip 0 width.</param>
        /// <param name="Height">Mip 0 height.</param>
        /// <param name="MipLevels">Mip level count.</param>
        /// <param name="ArrayLayers">Array layer count.</param>
        /// <param name="TotalBytes">What the incumbent allocates the whole staging <c>MTLBuffer</c> at.</param>
        /// <param name="MipLevel">Which mip level this row's layout is for.</param>
        /// <param name="ArrayLayer">Which array layer.</param>
        /// <param name="Offset">The subresource's byte offset from the buffer's first byte.</param>
        /// <param name="RowPitch">Bytes between rows, which is what a golden de-strides with.</param>
        /// <param name="DepthPitch">Bytes for the whole depth slice.</param>
        /// <param name="Size">The subresource's size, which is what a mapping reports.</param>
        public readonly record struct Row(
            GpuPixelFormat Format, uint Width, uint Height, uint MipLevels, uint ArrayLayers, ulong TotalBytes,
            uint MipLevel, uint ArrayLayer, ulong Offset, ulong RowPitch, ulong DepthPitch, ulong Size);

        /// <summary>
        /// Every row agrees: the total buffer size, the offset, the row pitch, the depth pitch and the size.
        /// One test over the whole table rather than a theory per row, because a theory with 232 cases makes a
        /// failure a needle and this reports every disagreement at once with its shape named.
        /// </summary>
        [Fact]
        public void EveryRow_MatchesTheIncumbentsSoftwareLayout()
        {
            var wrong = new List<string>();

            foreach (Row row in Table)
            {
                var shape = new MetalStagingShape(row.Width, row.Height, row.MipLevels, row.ArrayLayers,
                    row.Format);

                ulong total = MetalStagingLayout.TotalBytes(shape);
                if (total != row.TotalBytes) wrong.Add(Describe(row, "total size", row.TotalBytes, total));

                MetalSubresourceLayout layout = MetalStagingLayout.For(shape, row.MipLevel, row.ArrayLayer);

                if (layout.Offset != row.Offset) wrong.Add(Describe(row, "offset", row.Offset, layout.Offset));
                if (layout.RowPitch != row.RowPitch)
                    wrong.Add(Describe(row, "row pitch", row.RowPitch, layout.RowPitch));
                if (layout.DepthPitch != row.DepthPitch)
                    wrong.Add(Describe(row, "depth pitch", row.DepthPitch, layout.DepthPitch));
                if (layout.Size != row.Size) wrong.Add(Describe(row, "size", row.Size, layout.Size));
            }

            _output.WriteLine(Table.Count.ToString(CultureInfo.InvariantCulture) + " rows checked");

            Assert.True(wrong.Count == 0,
                "The native Metal staging layout disagrees with the incumbent's software arithmetic. Every "
                + "golden reads back through Map and MappedData.RowPitch, so this garbles all 36 at once and "
                + "does it silently.\n" + string.Join("\n", wrong));
        }

        /// <summary>
        /// THE TABLE COVERS WHAT IT CLAIMS TO, which is the control every table needs: a table that quietly lost
        /// its five-byte format, or its layered shapes, would still pass the row above.
        /// </summary>
        [Fact]
        public void TheTable_CoversEverySeamFormatAndBothKindsOfShape()
        {
            var formats = new HashSet<GpuPixelFormat>();
            bool mipped = false;
            bool layered = false;
            bool both = false;

            foreach (Row row in Table)
            {
                formats.Add(row.Format);
                if (row.MipLevels > 1) mipped = true;
                if (row.ArrayLayers > 1) layered = true;
                if (row.MipLevels > 1 && row.ArrayLayers > 1) both = true;
            }

            Assert.Equal(8, formats.Count);
            Assert.Contains(GpuPixelFormat.D32FloatS8UInt, formats);
            Assert.True(mipped, "no mipped shape in the table");
            Assert.True(layered, "no layered shape in the table");
            Assert.True(both, "no shape that is both mipped and layered, which is where a mip offset and a "
                + "layer pitch can disagree");
        }

        /// <summary>
        /// The FIVE-BYTE depth-stencil texel, on its own, because it is the single value a reader is most likely
        /// to "fix". The real <c>MTLPixelFormatDepth32Float_Stencil8</c> texture is eight bytes per texel with the
        /// stencil in its own plane, and this number is not about that texture: it is the incumbent's software
        /// layout for the <c>MTLBuffer</c> that mirrors it, and the goldens read back through the incumbent's
        /// stride rather than the driver's.
        /// </summary>
        [Fact]
        public void TheDepthStencilTexel_IsFiveBytes_BecauseTheIncumbentSaysSo()
        {
            Assert.Equal(5u, MetalStagingLayout.BytesPerTexel(GpuPixelFormat.D32FloatS8UInt));
            Assert.Equal(4u, MetalStagingLayout.BytesPerTexel(GpuPixelFormat.D24UNormS8UInt));
        }

        static string Describe(in Row row, string what, ulong expected, ulong actual)
            => row.Format + " " + row.Width + "x" + row.Height + " mips " + row.MipLevels + " layers "
                + row.ArrayLayers + " subresource (mip " + row.MipLevel + ", layer " + row.ArrayLayer + "): "
                + what + " expected " + expected + " and was " + actual;

        // ---- The table ------------------------------------------------------------------------------------
        //
        // Generated from Veldrid 4.9.103's own functions, not from the implementation under test. See the class
        // summary for the exact list of functions and how they compose.
        static readonly IReadOnlyList<Row> Table =
        [
            new Row(GpuPixelFormat.R8UNorm, 1, 1, 1, 1, 1, 0, 0, 0, 1, 1, 1),
            new Row(GpuPixelFormat.R8UNorm, 17, 5, 1, 1, 85, 0, 0, 0, 17, 85, 85),
            new Row(GpuPixelFormat.R8UNorm, 256, 128, 1, 1, 32768, 0, 0, 0, 256, 32768, 32768),
            new Row(GpuPixelFormat.R8UNorm, 64, 64, 7, 1, 5461, 0, 0, 0, 64, 4096, 4096),
            new Row(GpuPixelFormat.R8UNorm, 64, 64, 7, 1, 5461, 1, 0, 4096, 32, 1024, 1024),
            new Row(GpuPixelFormat.R8UNorm, 64, 64, 7, 1, 5461, 2, 0, 5120, 16, 256, 256),
            new Row(GpuPixelFormat.R8UNorm, 64, 64, 7, 1, 5461, 3, 0, 5376, 8, 64, 64),
            new Row(GpuPixelFormat.R8UNorm, 64, 64, 7, 1, 5461, 4, 0, 5440, 4, 16, 16),
            new Row(GpuPixelFormat.R8UNorm, 64, 64, 7, 1, 5461, 5, 0, 5456, 2, 4, 4),
            new Row(GpuPixelFormat.R8UNorm, 64, 64, 7, 1, 5461, 6, 0, 5460, 1, 1, 1),
            new Row(GpuPixelFormat.R8UNorm, 100, 60, 3, 1, 7875, 0, 0, 0, 100, 6000, 6000),
            new Row(GpuPixelFormat.R8UNorm, 100, 60, 3, 1, 7875, 1, 0, 6000, 50, 1500, 1500),
            new Row(GpuPixelFormat.R8UNorm, 100, 60, 3, 1, 7875, 2, 0, 7500, 25, 375, 375),
            new Row(GpuPixelFormat.R8UNorm, 64, 64, 1, 4, 16384, 0, 0, 0, 64, 4096, 4096),
            new Row(GpuPixelFormat.R8UNorm, 64, 64, 1, 4, 16384, 0, 1, 4096, 64, 4096, 4096),
            new Row(GpuPixelFormat.R8UNorm, 64, 64, 1, 4, 16384, 0, 2, 8192, 64, 4096, 4096),
            new Row(GpuPixelFormat.R8UNorm, 64, 64, 1, 4, 16384, 0, 3, 12288, 64, 4096, 4096),
            new Row(GpuPixelFormat.R8UNorm, 33, 17, 4, 3, 2187, 0, 0, 0, 33, 561, 561),
            new Row(GpuPixelFormat.R8UNorm, 33, 17, 4, 3, 2187, 1, 0, 561, 16, 128, 128),
            new Row(GpuPixelFormat.R8UNorm, 33, 17, 4, 3, 2187, 2, 0, 689, 8, 32, 32),
            new Row(GpuPixelFormat.R8UNorm, 33, 17, 4, 3, 2187, 3, 0, 721, 4, 8, 8),
            new Row(GpuPixelFormat.R8UNorm, 33, 17, 4, 3, 2187, 0, 1, 729, 33, 561, 561),
            new Row(GpuPixelFormat.R8UNorm, 33, 17, 4, 3, 2187, 1, 1, 1290, 16, 128, 128),
            new Row(GpuPixelFormat.R8UNorm, 33, 17, 4, 3, 2187, 2, 1, 1418, 8, 32, 32),
            new Row(GpuPixelFormat.R8UNorm, 33, 17, 4, 3, 2187, 3, 1, 1450, 4, 8, 8),
            new Row(GpuPixelFormat.R8UNorm, 33, 17, 4, 3, 2187, 0, 2, 1458, 33, 561, 561),
            new Row(GpuPixelFormat.R8UNorm, 33, 17, 4, 3, 2187, 1, 2, 2019, 16, 128, 128),
            new Row(GpuPixelFormat.R8UNorm, 33, 17, 4, 3, 2187, 2, 2, 2147, 8, 32, 32),
            new Row(GpuPixelFormat.R8UNorm, 33, 17, 4, 3, 2187, 3, 2, 2179, 4, 8, 8),
            new Row(GpuPixelFormat.R16G16Float, 1, 1, 1, 1, 4, 0, 0, 0, 4, 4, 4),
            new Row(GpuPixelFormat.R16G16Float, 17, 5, 1, 1, 340, 0, 0, 0, 68, 340, 340),
            new Row(GpuPixelFormat.R16G16Float, 256, 128, 1, 1, 131072, 0, 0, 0, 1024, 131072, 131072),
            new Row(GpuPixelFormat.R16G16Float, 64, 64, 7, 1, 21844, 0, 0, 0, 256, 16384, 16384),
            new Row(GpuPixelFormat.R16G16Float, 64, 64, 7, 1, 21844, 1, 0, 16384, 128, 4096, 4096),
            new Row(GpuPixelFormat.R16G16Float, 64, 64, 7, 1, 21844, 2, 0, 20480, 64, 1024, 1024),
            new Row(GpuPixelFormat.R16G16Float, 64, 64, 7, 1, 21844, 3, 0, 21504, 32, 256, 256),
            new Row(GpuPixelFormat.R16G16Float, 64, 64, 7, 1, 21844, 4, 0, 21760, 16, 64, 64),
            new Row(GpuPixelFormat.R16G16Float, 64, 64, 7, 1, 21844, 5, 0, 21824, 8, 16, 16),
            new Row(GpuPixelFormat.R16G16Float, 64, 64, 7, 1, 21844, 6, 0, 21840, 4, 4, 4),
            new Row(GpuPixelFormat.R16G16Float, 100, 60, 3, 1, 31500, 0, 0, 0, 400, 24000, 24000),
            new Row(GpuPixelFormat.R16G16Float, 100, 60, 3, 1, 31500, 1, 0, 24000, 200, 6000, 6000),
            new Row(GpuPixelFormat.R16G16Float, 100, 60, 3, 1, 31500, 2, 0, 30000, 100, 1500, 1500),
            new Row(GpuPixelFormat.R16G16Float, 64, 64, 1, 4, 65536, 0, 0, 0, 256, 16384, 16384),
            new Row(GpuPixelFormat.R16G16Float, 64, 64, 1, 4, 65536, 0, 1, 16384, 256, 16384, 16384),
            new Row(GpuPixelFormat.R16G16Float, 64, 64, 1, 4, 65536, 0, 2, 32768, 256, 16384, 16384),
            new Row(GpuPixelFormat.R16G16Float, 64, 64, 1, 4, 65536, 0, 3, 49152, 256, 16384, 16384),
            new Row(GpuPixelFormat.R16G16Float, 33, 17, 4, 3, 8748, 0, 0, 0, 132, 2244, 2244),
            new Row(GpuPixelFormat.R16G16Float, 33, 17, 4, 3, 8748, 1, 0, 2244, 64, 512, 512),
            new Row(GpuPixelFormat.R16G16Float, 33, 17, 4, 3, 8748, 2, 0, 2756, 32, 128, 128),
            new Row(GpuPixelFormat.R16G16Float, 33, 17, 4, 3, 8748, 3, 0, 2884, 16, 32, 32),
            new Row(GpuPixelFormat.R16G16Float, 33, 17, 4, 3, 8748, 0, 1, 2916, 132, 2244, 2244),
            new Row(GpuPixelFormat.R16G16Float, 33, 17, 4, 3, 8748, 1, 1, 5160, 64, 512, 512),
            new Row(GpuPixelFormat.R16G16Float, 33, 17, 4, 3, 8748, 2, 1, 5672, 32, 128, 128),
            new Row(GpuPixelFormat.R16G16Float, 33, 17, 4, 3, 8748, 3, 1, 5800, 16, 32, 32),
            new Row(GpuPixelFormat.R16G16Float, 33, 17, 4, 3, 8748, 0, 2, 5832, 132, 2244, 2244),
            new Row(GpuPixelFormat.R16G16Float, 33, 17, 4, 3, 8748, 1, 2, 8076, 64, 512, 512),
            new Row(GpuPixelFormat.R16G16Float, 33, 17, 4, 3, 8748, 2, 2, 8588, 32, 128, 128),
            new Row(GpuPixelFormat.R16G16Float, 33, 17, 4, 3, 8748, 3, 2, 8716, 16, 32, 32),
            new Row(GpuPixelFormat.R32Float, 1, 1, 1, 1, 4, 0, 0, 0, 4, 4, 4),
            new Row(GpuPixelFormat.R32Float, 17, 5, 1, 1, 340, 0, 0, 0, 68, 340, 340),
            new Row(GpuPixelFormat.R32Float, 256, 128, 1, 1, 131072, 0, 0, 0, 1024, 131072, 131072),
            new Row(GpuPixelFormat.R32Float, 64, 64, 7, 1, 21844, 0, 0, 0, 256, 16384, 16384),
            new Row(GpuPixelFormat.R32Float, 64, 64, 7, 1, 21844, 1, 0, 16384, 128, 4096, 4096),
            new Row(GpuPixelFormat.R32Float, 64, 64, 7, 1, 21844, 2, 0, 20480, 64, 1024, 1024),
            new Row(GpuPixelFormat.R32Float, 64, 64, 7, 1, 21844, 3, 0, 21504, 32, 256, 256),
            new Row(GpuPixelFormat.R32Float, 64, 64, 7, 1, 21844, 4, 0, 21760, 16, 64, 64),
            new Row(GpuPixelFormat.R32Float, 64, 64, 7, 1, 21844, 5, 0, 21824, 8, 16, 16),
            new Row(GpuPixelFormat.R32Float, 64, 64, 7, 1, 21844, 6, 0, 21840, 4, 4, 4),
            new Row(GpuPixelFormat.R32Float, 100, 60, 3, 1, 31500, 0, 0, 0, 400, 24000, 24000),
            new Row(GpuPixelFormat.R32Float, 100, 60, 3, 1, 31500, 1, 0, 24000, 200, 6000, 6000),
            new Row(GpuPixelFormat.R32Float, 100, 60, 3, 1, 31500, 2, 0, 30000, 100, 1500, 1500),
            new Row(GpuPixelFormat.R32Float, 64, 64, 1, 4, 65536, 0, 0, 0, 256, 16384, 16384),
            new Row(GpuPixelFormat.R32Float, 64, 64, 1, 4, 65536, 0, 1, 16384, 256, 16384, 16384),
            new Row(GpuPixelFormat.R32Float, 64, 64, 1, 4, 65536, 0, 2, 32768, 256, 16384, 16384),
            new Row(GpuPixelFormat.R32Float, 64, 64, 1, 4, 65536, 0, 3, 49152, 256, 16384, 16384),
            new Row(GpuPixelFormat.R32Float, 33, 17, 4, 3, 8748, 0, 0, 0, 132, 2244, 2244),
            new Row(GpuPixelFormat.R32Float, 33, 17, 4, 3, 8748, 1, 0, 2244, 64, 512, 512),
            new Row(GpuPixelFormat.R32Float, 33, 17, 4, 3, 8748, 2, 0, 2756, 32, 128, 128),
            new Row(GpuPixelFormat.R32Float, 33, 17, 4, 3, 8748, 3, 0, 2884, 16, 32, 32),
            new Row(GpuPixelFormat.R32Float, 33, 17, 4, 3, 8748, 0, 1, 2916, 132, 2244, 2244),
            new Row(GpuPixelFormat.R32Float, 33, 17, 4, 3, 8748, 1, 1, 5160, 64, 512, 512),
            new Row(GpuPixelFormat.R32Float, 33, 17, 4, 3, 8748, 2, 1, 5672, 32, 128, 128),
            new Row(GpuPixelFormat.R32Float, 33, 17, 4, 3, 8748, 3, 1, 5800, 16, 32, 32),
            new Row(GpuPixelFormat.R32Float, 33, 17, 4, 3, 8748, 0, 2, 5832, 132, 2244, 2244),
            new Row(GpuPixelFormat.R32Float, 33, 17, 4, 3, 8748, 1, 2, 8076, 64, 512, 512),
            new Row(GpuPixelFormat.R32Float, 33, 17, 4, 3, 8748, 2, 2, 8588, 32, 128, 128),
            new Row(GpuPixelFormat.R32Float, 33, 17, 4, 3, 8748, 3, 2, 8716, 16, 32, 32),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 1, 1, 1, 1, 4, 0, 0, 0, 4, 4, 4),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 17, 5, 1, 1, 340, 0, 0, 0, 68, 340, 340),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 256, 128, 1, 1, 131072, 0, 0, 0, 1024, 131072, 131072),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 64, 64, 7, 1, 21844, 0, 0, 0, 256, 16384, 16384),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 64, 64, 7, 1, 21844, 1, 0, 16384, 128, 4096, 4096),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 64, 64, 7, 1, 21844, 2, 0, 20480, 64, 1024, 1024),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 64, 64, 7, 1, 21844, 3, 0, 21504, 32, 256, 256),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 64, 64, 7, 1, 21844, 4, 0, 21760, 16, 64, 64),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 64, 64, 7, 1, 21844, 5, 0, 21824, 8, 16, 16),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 64, 64, 7, 1, 21844, 6, 0, 21840, 4, 4, 4),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 100, 60, 3, 1, 31500, 0, 0, 0, 400, 24000, 24000),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 100, 60, 3, 1, 31500, 1, 0, 24000, 200, 6000, 6000),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 100, 60, 3, 1, 31500, 2, 0, 30000, 100, 1500, 1500),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 64, 64, 1, 4, 65536, 0, 0, 0, 256, 16384, 16384),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 64, 64, 1, 4, 65536, 0, 1, 16384, 256, 16384, 16384),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 64, 64, 1, 4, 65536, 0, 2, 32768, 256, 16384, 16384),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 64, 64, 1, 4, 65536, 0, 3, 49152, 256, 16384, 16384),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 33, 17, 4, 3, 8748, 0, 0, 0, 132, 2244, 2244),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 33, 17, 4, 3, 8748, 1, 0, 2244, 64, 512, 512),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 33, 17, 4, 3, 8748, 2, 0, 2756, 32, 128, 128),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 33, 17, 4, 3, 8748, 3, 0, 2884, 16, 32, 32),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 33, 17, 4, 3, 8748, 0, 1, 2916, 132, 2244, 2244),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 33, 17, 4, 3, 8748, 1, 1, 5160, 64, 512, 512),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 33, 17, 4, 3, 8748, 2, 1, 5672, 32, 128, 128),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 33, 17, 4, 3, 8748, 3, 1, 5800, 16, 32, 32),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 33, 17, 4, 3, 8748, 0, 2, 5832, 132, 2244, 2244),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 33, 17, 4, 3, 8748, 1, 2, 8076, 64, 512, 512),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 33, 17, 4, 3, 8748, 2, 2, 8588, 32, 128, 128),
            new Row(GpuPixelFormat.R8G8B8A8UNorm, 33, 17, 4, 3, 8748, 3, 2, 8716, 16, 32, 32),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 1, 1, 1, 1, 4, 0, 0, 0, 4, 4, 4),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 17, 5, 1, 1, 340, 0, 0, 0, 68, 340, 340),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 256, 128, 1, 1, 131072, 0, 0, 0, 1024, 131072, 131072),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 64, 64, 7, 1, 21844, 0, 0, 0, 256, 16384, 16384),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 64, 64, 7, 1, 21844, 1, 0, 16384, 128, 4096, 4096),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 64, 64, 7, 1, 21844, 2, 0, 20480, 64, 1024, 1024),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 64, 64, 7, 1, 21844, 3, 0, 21504, 32, 256, 256),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 64, 64, 7, 1, 21844, 4, 0, 21760, 16, 64, 64),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 64, 64, 7, 1, 21844, 5, 0, 21824, 8, 16, 16),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 64, 64, 7, 1, 21844, 6, 0, 21840, 4, 4, 4),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 100, 60, 3, 1, 31500, 0, 0, 0, 400, 24000, 24000),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 100, 60, 3, 1, 31500, 1, 0, 24000, 200, 6000, 6000),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 100, 60, 3, 1, 31500, 2, 0, 30000, 100, 1500, 1500),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 64, 64, 1, 4, 65536, 0, 0, 0, 256, 16384, 16384),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 64, 64, 1, 4, 65536, 0, 1, 16384, 256, 16384, 16384),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 64, 64, 1, 4, 65536, 0, 2, 32768, 256, 16384, 16384),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 64, 64, 1, 4, 65536, 0, 3, 49152, 256, 16384, 16384),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 33, 17, 4, 3, 8748, 0, 0, 0, 132, 2244, 2244),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 33, 17, 4, 3, 8748, 1, 0, 2244, 64, 512, 512),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 33, 17, 4, 3, 8748, 2, 0, 2756, 32, 128, 128),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 33, 17, 4, 3, 8748, 3, 0, 2884, 16, 32, 32),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 33, 17, 4, 3, 8748, 0, 1, 2916, 132, 2244, 2244),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 33, 17, 4, 3, 8748, 1, 1, 5160, 64, 512, 512),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 33, 17, 4, 3, 8748, 2, 1, 5672, 32, 128, 128),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 33, 17, 4, 3, 8748, 3, 1, 5800, 16, 32, 32),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 33, 17, 4, 3, 8748, 0, 2, 5832, 132, 2244, 2244),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 33, 17, 4, 3, 8748, 1, 2, 8076, 64, 512, 512),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 33, 17, 4, 3, 8748, 2, 2, 8588, 32, 128, 128),
            new Row(GpuPixelFormat.B8G8R8A8UNorm, 33, 17, 4, 3, 8748, 3, 2, 8716, 16, 32, 32),
            new Row(GpuPixelFormat.D24UNormS8UInt, 1, 1, 1, 1, 4, 0, 0, 0, 4, 4, 4),
            new Row(GpuPixelFormat.D24UNormS8UInt, 17, 5, 1, 1, 340, 0, 0, 0, 68, 340, 340),
            new Row(GpuPixelFormat.D24UNormS8UInt, 256, 128, 1, 1, 131072, 0, 0, 0, 1024, 131072, 131072),
            new Row(GpuPixelFormat.D24UNormS8UInt, 64, 64, 7, 1, 21844, 0, 0, 0, 256, 16384, 16384),
            new Row(GpuPixelFormat.D24UNormS8UInt, 64, 64, 7, 1, 21844, 1, 0, 16384, 128, 4096, 4096),
            new Row(GpuPixelFormat.D24UNormS8UInt, 64, 64, 7, 1, 21844, 2, 0, 20480, 64, 1024, 1024),
            new Row(GpuPixelFormat.D24UNormS8UInt, 64, 64, 7, 1, 21844, 3, 0, 21504, 32, 256, 256),
            new Row(GpuPixelFormat.D24UNormS8UInt, 64, 64, 7, 1, 21844, 4, 0, 21760, 16, 64, 64),
            new Row(GpuPixelFormat.D24UNormS8UInt, 64, 64, 7, 1, 21844, 5, 0, 21824, 8, 16, 16),
            new Row(GpuPixelFormat.D24UNormS8UInt, 64, 64, 7, 1, 21844, 6, 0, 21840, 4, 4, 4),
            new Row(GpuPixelFormat.D24UNormS8UInt, 100, 60, 3, 1, 31500, 0, 0, 0, 400, 24000, 24000),
            new Row(GpuPixelFormat.D24UNormS8UInt, 100, 60, 3, 1, 31500, 1, 0, 24000, 200, 6000, 6000),
            new Row(GpuPixelFormat.D24UNormS8UInt, 100, 60, 3, 1, 31500, 2, 0, 30000, 100, 1500, 1500),
            new Row(GpuPixelFormat.D24UNormS8UInt, 64, 64, 1, 4, 65536, 0, 0, 0, 256, 16384, 16384),
            new Row(GpuPixelFormat.D24UNormS8UInt, 64, 64, 1, 4, 65536, 0, 1, 16384, 256, 16384, 16384),
            new Row(GpuPixelFormat.D24UNormS8UInt, 64, 64, 1, 4, 65536, 0, 2, 32768, 256, 16384, 16384),
            new Row(GpuPixelFormat.D24UNormS8UInt, 64, 64, 1, 4, 65536, 0, 3, 49152, 256, 16384, 16384),
            new Row(GpuPixelFormat.D24UNormS8UInt, 33, 17, 4, 3, 8748, 0, 0, 0, 132, 2244, 2244),
            new Row(GpuPixelFormat.D24UNormS8UInt, 33, 17, 4, 3, 8748, 1, 0, 2244, 64, 512, 512),
            new Row(GpuPixelFormat.D24UNormS8UInt, 33, 17, 4, 3, 8748, 2, 0, 2756, 32, 128, 128),
            new Row(GpuPixelFormat.D24UNormS8UInt, 33, 17, 4, 3, 8748, 3, 0, 2884, 16, 32, 32),
            new Row(GpuPixelFormat.D24UNormS8UInt, 33, 17, 4, 3, 8748, 0, 1, 2916, 132, 2244, 2244),
            new Row(GpuPixelFormat.D24UNormS8UInt, 33, 17, 4, 3, 8748, 1, 1, 5160, 64, 512, 512),
            new Row(GpuPixelFormat.D24UNormS8UInt, 33, 17, 4, 3, 8748, 2, 1, 5672, 32, 128, 128),
            new Row(GpuPixelFormat.D24UNormS8UInt, 33, 17, 4, 3, 8748, 3, 1, 5800, 16, 32, 32),
            new Row(GpuPixelFormat.D24UNormS8UInt, 33, 17, 4, 3, 8748, 0, 2, 5832, 132, 2244, 2244),
            new Row(GpuPixelFormat.D24UNormS8UInt, 33, 17, 4, 3, 8748, 1, 2, 8076, 64, 512, 512),
            new Row(GpuPixelFormat.D24UNormS8UInt, 33, 17, 4, 3, 8748, 2, 2, 8588, 32, 128, 128),
            new Row(GpuPixelFormat.D24UNormS8UInt, 33, 17, 4, 3, 8748, 3, 2, 8716, 16, 32, 32),
            new Row(GpuPixelFormat.D32FloatS8UInt, 1, 1, 1, 1, 5, 0, 0, 0, 5, 5, 5),
            new Row(GpuPixelFormat.D32FloatS8UInt, 17, 5, 1, 1, 425, 0, 0, 0, 85, 425, 425),
            new Row(GpuPixelFormat.D32FloatS8UInt, 256, 128, 1, 1, 163840, 0, 0, 0, 1280, 163840, 163840),
            new Row(GpuPixelFormat.D32FloatS8UInt, 64, 64, 7, 1, 27305, 0, 0, 0, 320, 20480, 20480),
            new Row(GpuPixelFormat.D32FloatS8UInt, 64, 64, 7, 1, 27305, 1, 0, 20480, 160, 5120, 5120),
            new Row(GpuPixelFormat.D32FloatS8UInt, 64, 64, 7, 1, 27305, 2, 0, 25600, 80, 1280, 1280),
            new Row(GpuPixelFormat.D32FloatS8UInt, 64, 64, 7, 1, 27305, 3, 0, 26880, 40, 320, 320),
            new Row(GpuPixelFormat.D32FloatS8UInt, 64, 64, 7, 1, 27305, 4, 0, 27200, 20, 80, 80),
            new Row(GpuPixelFormat.D32FloatS8UInt, 64, 64, 7, 1, 27305, 5, 0, 27280, 10, 20, 20),
            new Row(GpuPixelFormat.D32FloatS8UInt, 64, 64, 7, 1, 27305, 6, 0, 27300, 5, 5, 5),
            new Row(GpuPixelFormat.D32FloatS8UInt, 100, 60, 3, 1, 39375, 0, 0, 0, 500, 30000, 30000),
            new Row(GpuPixelFormat.D32FloatS8UInt, 100, 60, 3, 1, 39375, 1, 0, 30000, 250, 7500, 7500),
            new Row(GpuPixelFormat.D32FloatS8UInt, 100, 60, 3, 1, 39375, 2, 0, 37500, 125, 1875, 1875),
            new Row(GpuPixelFormat.D32FloatS8UInt, 64, 64, 1, 4, 81920, 0, 0, 0, 320, 20480, 20480),
            new Row(GpuPixelFormat.D32FloatS8UInt, 64, 64, 1, 4, 81920, 0, 1, 20480, 320, 20480, 20480),
            new Row(GpuPixelFormat.D32FloatS8UInt, 64, 64, 1, 4, 81920, 0, 2, 40960, 320, 20480, 20480),
            new Row(GpuPixelFormat.D32FloatS8UInt, 64, 64, 1, 4, 81920, 0, 3, 61440, 320, 20480, 20480),
            new Row(GpuPixelFormat.D32FloatS8UInt, 33, 17, 4, 3, 10935, 0, 0, 0, 165, 2805, 2805),
            new Row(GpuPixelFormat.D32FloatS8UInt, 33, 17, 4, 3, 10935, 1, 0, 2805, 80, 640, 640),
            new Row(GpuPixelFormat.D32FloatS8UInt, 33, 17, 4, 3, 10935, 2, 0, 3445, 40, 160, 160),
            new Row(GpuPixelFormat.D32FloatS8UInt, 33, 17, 4, 3, 10935, 3, 0, 3605, 20, 40, 40),
            new Row(GpuPixelFormat.D32FloatS8UInt, 33, 17, 4, 3, 10935, 0, 1, 3645, 165, 2805, 2805),
            new Row(GpuPixelFormat.D32FloatS8UInt, 33, 17, 4, 3, 10935, 1, 1, 6450, 80, 640, 640),
            new Row(GpuPixelFormat.D32FloatS8UInt, 33, 17, 4, 3, 10935, 2, 1, 7090, 40, 160, 160),
            new Row(GpuPixelFormat.D32FloatS8UInt, 33, 17, 4, 3, 10935, 3, 1, 7250, 20, 40, 40),
            new Row(GpuPixelFormat.D32FloatS8UInt, 33, 17, 4, 3, 10935, 0, 2, 7290, 165, 2805, 2805),
            new Row(GpuPixelFormat.D32FloatS8UInt, 33, 17, 4, 3, 10935, 1, 2, 10095, 80, 640, 640),
            new Row(GpuPixelFormat.D32FloatS8UInt, 33, 17, 4, 3, 10935, 2, 2, 10735, 40, 160, 160),
            new Row(GpuPixelFormat.D32FloatS8UInt, 33, 17, 4, 3, 10935, 3, 2, 10895, 20, 40, 40),
            new Row(GpuPixelFormat.R16G16B16A16Float, 1, 1, 1, 1, 8, 0, 0, 0, 8, 8, 8),
            new Row(GpuPixelFormat.R16G16B16A16Float, 17, 5, 1, 1, 680, 0, 0, 0, 136, 680, 680),
            new Row(GpuPixelFormat.R16G16B16A16Float, 256, 128, 1, 1, 262144, 0, 0, 0, 2048, 262144, 262144),
            new Row(GpuPixelFormat.R16G16B16A16Float, 64, 64, 7, 1, 43688, 0, 0, 0, 512, 32768, 32768),
            new Row(GpuPixelFormat.R16G16B16A16Float, 64, 64, 7, 1, 43688, 1, 0, 32768, 256, 8192, 8192),
            new Row(GpuPixelFormat.R16G16B16A16Float, 64, 64, 7, 1, 43688, 2, 0, 40960, 128, 2048, 2048),
            new Row(GpuPixelFormat.R16G16B16A16Float, 64, 64, 7, 1, 43688, 3, 0, 43008, 64, 512, 512),
            new Row(GpuPixelFormat.R16G16B16A16Float, 64, 64, 7, 1, 43688, 4, 0, 43520, 32, 128, 128),
            new Row(GpuPixelFormat.R16G16B16A16Float, 64, 64, 7, 1, 43688, 5, 0, 43648, 16, 32, 32),
            new Row(GpuPixelFormat.R16G16B16A16Float, 64, 64, 7, 1, 43688, 6, 0, 43680, 8, 8, 8),
            new Row(GpuPixelFormat.R16G16B16A16Float, 100, 60, 3, 1, 63000, 0, 0, 0, 800, 48000, 48000),
            new Row(GpuPixelFormat.R16G16B16A16Float, 100, 60, 3, 1, 63000, 1, 0, 48000, 400, 12000, 12000),
            new Row(GpuPixelFormat.R16G16B16A16Float, 100, 60, 3, 1, 63000, 2, 0, 60000, 200, 3000, 3000),
            new Row(GpuPixelFormat.R16G16B16A16Float, 64, 64, 1, 4, 131072, 0, 0, 0, 512, 32768, 32768),
            new Row(GpuPixelFormat.R16G16B16A16Float, 64, 64, 1, 4, 131072, 0, 1, 32768, 512, 32768, 32768),
            new Row(GpuPixelFormat.R16G16B16A16Float, 64, 64, 1, 4, 131072, 0, 2, 65536, 512, 32768, 32768),
            new Row(GpuPixelFormat.R16G16B16A16Float, 64, 64, 1, 4, 131072, 0, 3, 98304, 512, 32768, 32768),
            new Row(GpuPixelFormat.R16G16B16A16Float, 33, 17, 4, 3, 17496, 0, 0, 0, 264, 4488, 4488),
            new Row(GpuPixelFormat.R16G16B16A16Float, 33, 17, 4, 3, 17496, 1, 0, 4488, 128, 1024, 1024),
            new Row(GpuPixelFormat.R16G16B16A16Float, 33, 17, 4, 3, 17496, 2, 0, 5512, 64, 256, 256),
            new Row(GpuPixelFormat.R16G16B16A16Float, 33, 17, 4, 3, 17496, 3, 0, 5768, 32, 64, 64),
            new Row(GpuPixelFormat.R16G16B16A16Float, 33, 17, 4, 3, 17496, 0, 1, 5832, 264, 4488, 4488),
            new Row(GpuPixelFormat.R16G16B16A16Float, 33, 17, 4, 3, 17496, 1, 1, 10320, 128, 1024, 1024),
            new Row(GpuPixelFormat.R16G16B16A16Float, 33, 17, 4, 3, 17496, 2, 1, 11344, 64, 256, 256),
            new Row(GpuPixelFormat.R16G16B16A16Float, 33, 17, 4, 3, 17496, 3, 1, 11600, 32, 64, 64),
            new Row(GpuPixelFormat.R16G16B16A16Float, 33, 17, 4, 3, 17496, 0, 2, 11664, 264, 4488, 4488),
            new Row(GpuPixelFormat.R16G16B16A16Float, 33, 17, 4, 3, 17496, 1, 2, 16152, 128, 1024, 1024),
            new Row(GpuPixelFormat.R16G16B16A16Float, 33, 17, 4, 3, 17496, 2, 2, 17176, 64, 256, 256),
            new Row(GpuPixelFormat.R16G16B16A16Float, 33, 17, 4, 3, 17496, 3, 2, 17432, 32, 64, 64),
        ];
    }
}
