using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.D3D11;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// EVERY VORTICE STRUCT THE BACKEND STILL HANDS THE DRIVER BY ADDRESS HAS THE NATIVE SIZE
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/1192).
    /// <para>
    /// Vortice passes these structs' managed addresses to the driver with no marshalling, so the managed layout IS
    /// the ABI. <c>SamplerDescription</c> broke that when Vortice.Mathematics made <c>Color4</c> a
    /// <c>Vector128&lt;float&gt;</c>, and the backend stopped using it. The ones left were audited against their
    /// <c>d3d11.h</c> and <c>dxgi.h</c> sizes and match, and this fact pins each one so a Vortice or
    /// Vortice.Mathematics upgrade that moves one goes red here rather than on a driver.
    /// </para>
    /// <para>
    /// WINDOWS ONLY, BY NECESSITY. Naming a Vortice type puts the interop into the process, and the suite asserts
    /// off Windows that nothing does (<see cref="D3D11InteropLoad"/>). The body that names them is
    /// <c>NoInlining</c> behind <see cref="KhaozEngineD3D11.IsPlatformSupported"/>, the pattern the backend itself
    /// uses. Structs Vortice copies into its own generated native form first (<c>BlendDescription</c>,
    /// <c>InputElementDescription</c> and the adapter and message descriptions) are not passed by address and are
    /// not pinned, apart from the blend description's per-target element, which sits inside that native form.
    /// </para>
    /// </summary>
    public sealed class D3D11VorticeStructLayoutTests
    {
        [Fact]
        public void OnWindows_EveryVorticeStructPassedByAddress_HasItsNativeSize()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) return;   // naming a Vortice type here would load it
            AssertNativeSizesWindows();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static void AssertNativeSizesWindows()
        {
            bool is64 = IntPtr.Size == 8;

            // Vortice.Mathematics and Vortice.DirectX value types handed to context calls.
            Assert.Equal(16, Unsafe.SizeOf<Vortice.Mathematics.Color4>());       // FLOAT[4] clear colour, blend factor
            Assert.Equal(24, Unsafe.SizeOf<Vortice.Mathematics.Viewport>());     // D3D11_VIEWPORT
            Assert.Equal(16, Unsafe.SizeOf<Vortice.RawRect>());                  // D3D11_RECT
            Assert.Equal(24, Unsafe.SizeOf<Vortice.Mathematics.Box>());          // D3D11_BOX

            // Descriptions handed to device creation calls, and the two read back.
            Assert.Equal(52, Unsafe.SizeOf<Vortice.Direct3D11.DepthStencilDescription>());
            Assert.Equal(40, Unsafe.SizeOf<Vortice.Direct3D11.RasterizerDescription>());
            Assert.Equal(32, Unsafe.SizeOf<Vortice.Direct3D11.RenderTargetBlendDescription>());
            Assert.Equal(44, Unsafe.SizeOf<Vortice.Direct3D11.Texture2DDescription>());
            Assert.Equal(24, Unsafe.SizeOf<Vortice.Direct3D11.BufferDescription>());
            Assert.Equal(24, Unsafe.SizeOf<Vortice.Direct3D11.ShaderResourceViewDescription>());
            Assert.Equal(20, Unsafe.SizeOf<Vortice.Direct3D11.UnorderedAccessViewDescription>());
            Assert.Equal(20, Unsafe.SizeOf<Vortice.Direct3D11.RenderTargetViewDescription>());
            Assert.Equal(24, Unsafe.SizeOf<Vortice.Direct3D11.DepthStencilViewDescription>());
            Assert.Equal(8, Unsafe.SizeOf<Vortice.Direct3D11.QueryDescription>());
            Assert.Equal(is64 ? 72 : 60, Unsafe.SizeOf<Vortice.DXGI.SwapChainDescription>());
            Assert.Equal(is64 ? 16 : 12, Unsafe.SizeOf<Vortice.Direct3D11.MappedSubresource>());
            Assert.Equal(56, Unsafe.SizeOf<Vortice.Direct3D11.FeatureDataD3D11Options>());
            Assert.Equal(8, Unsafe.SizeOf<Vortice.Direct3D11.FeatureDataThreading>());

            // The clear colour and blend factor go through as float*, so the component order matters too.
            var colour = new Vortice.Mathematics.Color4(1f, 2f, 3f, 4f);
            ReadOnlySpan<float> floats = MemoryMarshal.Cast<Vortice.Mathematics.Color4, float>(
                MemoryMarshal.CreateReadOnlySpan(ref colour, 1));
            Assert.Equal(new[] { 1f, 2f, 3f, 4f }, floats.ToArray());
        }
    }
}
