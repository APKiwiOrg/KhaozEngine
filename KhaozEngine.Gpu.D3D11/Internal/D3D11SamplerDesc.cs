using System.Runtime.InteropServices;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// The engine's own copy of the native <c>D3D11_SAMPLER_DESC</c>, and the only sampler description that
    /// reaches the driver (https://github.com/APKiwiOrg/KhaozEngine/issues/1192).
    /// <para>
    /// VORTICE'S <c>SamplerDescription</c> DOES NOT MATCH THE HEADER, and it is not used for that reason. Its
    /// <c>BorderColor</c> is a <c>Vortice.Mathematics.Color4</c>, whose net6.0 and net7.0 builds hold one
    /// <c>Vector128&lt;float&gt;</c>. The vector's 16-byte alignment moves the border colour from offset 28 to 32
    /// and grows the struct from 52 bytes to 64, and Vortice hands the managed struct's address to the driver
    /// with no marshalling. The driver read MinLOD from the border colour's alpha and MaxLOD from the managed
    /// MinLOD, both zero, so every sampler the backend made clamped to mip level 0.
    /// </para>
    /// <para>
    /// EVERY FIELD IS A PLAIN <c>int</c>, <c>uint</c> OR <c>float</c> under <c>Pack = 4</c>, so the layout is the
    /// header's on every runtime and no Vortice type is named. That also keeps this type off the interop load
    /// path, which the backend's off-Windows load tests require of every type in the package. The enum fields
    /// hold the native values, which Vortice's own enums share, so the Windows-only caller casts them across.
    /// <c>D3D11SamplerDescLayoutTests</c> pins the size and every offset against the header.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct D3D11SamplerDesc
    {
        /// <summary><c>D3D11_COMPARISON_NEVER</c>. The engine has no comparison sampler.</summary>
        internal const int ComparisonNever = 1;

        /// <summary><c>D3D11_FILTER</c>, offset 0.</summary>
        internal int Filter;

        /// <summary><c>D3D11_TEXTURE_ADDRESS_MODE</c>, offset 4.</summary>
        internal int AddressU;

        /// <summary><c>D3D11_TEXTURE_ADDRESS_MODE</c>, offset 8.</summary>
        internal int AddressV;

        /// <summary><c>D3D11_TEXTURE_ADDRESS_MODE</c>, offset 12.</summary>
        internal int AddressW;

        /// <summary><c>FLOAT MipLODBias</c>, offset 16.</summary>
        internal float MipLodBias;

        /// <summary><c>UINT MaxAnisotropy</c>, offset 20.</summary>
        internal uint MaxAnisotropy;

        /// <summary><c>D3D11_COMPARISON_FUNC</c>, offset 24.</summary>
        internal int ComparisonFunc;

        /// <summary><c>FLOAT BorderColor[4]</c>, offsets 28, 32, 36 and 40. Four named floats rather than a fixed
        /// buffer, because a fixed buffer would make this an unsafe type for no gain.</summary>
        internal float BorderColorR;

        /// <summary>Border colour green, offset 32.</summary>
        internal float BorderColorG;

        /// <summary>Border colour blue, offset 36.</summary>
        internal float BorderColorB;

        /// <summary>Border colour alpha, offset 40.</summary>
        internal float BorderColorA;

        /// <summary><c>FLOAT MinLOD</c>, offset 44.</summary>
        internal float MinLod;

        /// <summary><c>FLOAT MaxLOD</c>, offset 48.</summary>
        internal float MaxLod;

        /// <summary>
        /// A description with the four values the seam does not expose held where the backend has always held
        /// them: no comparison, a minimum LOD of 0, a maximum LOD of <c>uint.MaxValue</c> which Direct3D clamps
        /// to the real chain, and a transparent-black border. The caller supplies the native filter and address
        /// values, which it reads from Vortice's enums inside its Windows-only body.
        /// </summary>
        internal static D3D11SamplerDesc Create(int filter, int addressU, int addressV, int addressW,
            uint maxAnisotropy, float mipLodBias) => new()
            {
                Filter = filter,
                AddressU = addressU,
                AddressV = addressV,
                AddressW = addressW,
                MipLodBias = mipLodBias,
                MaxAnisotropy = maxAnisotropy,
                ComparisonFunc = ComparisonNever,
                MinLod = 0f,
                MaxLod = uint.MaxValue,
            };
    }
}
