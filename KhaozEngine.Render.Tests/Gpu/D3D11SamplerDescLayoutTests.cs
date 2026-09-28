using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SAMPLER DESCRIPTION THE DRIVER READS HAS THE HEADER'S LAYOUT
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/1192).
    /// <para>
    /// Every Direct3D 11 sampler reached the driver with a maximum LOD of 0 because Vortice's
    /// <c>SamplerDescription</c> is 64 bytes against the header's 52, with the border colour at 32 rather than 28.
    /// The backend now hands the driver <see cref="D3D11SamplerDesc"/>, and these facts pin its size and every
    /// offset to <c>D3D11_SAMPLER_DESC</c> in <c>d3d11.h</c>. They are device-free and run on every OS, because
    /// the struct names no Vortice type.
    /// </para>
    /// </summary>
    public sealed class D3D11SamplerDescLayoutTests
    {
        [Fact]
        public void Mirror_Is52Bytes_LikeTheNativeHeader()
        {
            Assert.Equal(52, Unsafe.SizeOf<D3D11SamplerDesc>());
            Assert.Equal(52, Marshal.SizeOf<D3D11SamplerDesc>());
        }

        [Fact]
        public void Mirror_EveryFieldSitsAtItsNativeOffset()
        {
            var d = default(D3D11SamplerDesc);

            Assert.Equal(0, Offset(ref d, ref d.Filter));
            Assert.Equal(4, Offset(ref d, ref d.AddressU));
            Assert.Equal(8, Offset(ref d, ref d.AddressV));
            Assert.Equal(12, Offset(ref d, ref d.AddressW));
            Assert.Equal(16, Offset(ref d, ref d.MipLodBias));
            Assert.Equal(20, Offset(ref d, ref d.MaxAnisotropy));
            Assert.Equal(24, Offset(ref d, ref d.ComparisonFunc));
            Assert.Equal(28, Offset(ref d, ref d.BorderColorR));
            Assert.Equal(32, Offset(ref d, ref d.BorderColorG));
            Assert.Equal(36, Offset(ref d, ref d.BorderColorB));
            Assert.Equal(40, Offset(ref d, ref d.BorderColorA));
            Assert.Equal(44, Offset(ref d, ref d.MinLod));
            Assert.Equal(48, Offset(ref d, ref d.MaxLod));
        }

        /// <summary>The four values the seam does not expose, and the six it does, land in the fields the driver
        /// reads. MaxLOD is the one #1192 lost, so it is read back through the raw bytes at offset 48 as
        /// well.</summary>
        [Fact]
        public void Create_HoldsTheHardcodedFour_AndPassesTheRestThrough()
        {
            D3D11SamplerDesc d = D3D11SamplerDesc.Create(filter: 0x55, addressU: 1, addressV: 3, addressW: 4,
                maxAnisotropy: 16, mipLodBias: 1f);

            Assert.Equal(0x55, d.Filter);
            Assert.Equal(1, d.AddressU);
            Assert.Equal(3, d.AddressV);
            Assert.Equal(4, d.AddressW);
            Assert.Equal(16u, d.MaxAnisotropy);
            Assert.Equal(1f, d.MipLodBias);
            Assert.Equal(1, d.ComparisonFunc);   // D3D11_COMPARISON_NEVER
            Assert.Equal(0f, d.BorderColorR + d.BorderColorG + d.BorderColorB + d.BorderColorA);
            Assert.Equal(0f, d.MinLod);
            Assert.Equal((float)uint.MaxValue, d.MaxLod);

            byte[] raw = new byte[52];
            MemoryMarshal.Write(raw, in d);
            Assert.Equal((float)uint.MaxValue, MemoryMarshal.Read<float>(raw.AsSpan(48)));
            Assert.Equal(0f, MemoryMarshal.Read<float>(raw.AsSpan(44)));

            if (!KhaozEngineD3D11.IsPlatformSupported) D3D11InteropLoad.AssertNotLoaded();
        }

        static int Offset<TField>(ref D3D11SamplerDesc owner, ref TField field)
            => (int)Unsafe.ByteOffset(ref Unsafe.As<D3D11SamplerDesc, byte>(ref owner),
                ref Unsafe.As<TField, byte>(ref field));
    }
}
