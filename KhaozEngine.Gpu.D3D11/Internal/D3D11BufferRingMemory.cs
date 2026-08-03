using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE SHIPPED <see cref="ID3D11RingMemory"/>: <c>ID3D11DeviceContext.Map</c> with
    /// <c>MAP_WRITE_NO_OVERWRITE</c> over one dynamic constant buffer, and the matching <c>Unmap</c>. Two native
    /// calls and nothing else, which is the entire point of the interface it implements.
    /// <para>
    /// THE MAPPED POINTER IS NOT STORED HERE and neither is the <c>MappedSubresource</c> it came in. That struct
    /// is a Vortice VALUE TYPE, and a field of one would make the CLR resolve the interop assembly merely to
    /// compute this type's layout, which puts Vortice on the load path of every macOS and Linux test run. The
    /// pointer is an <see cref="IntPtr"/> the moment it leaves the guarded body, and <see cref="D3D11UniformRing"/>
    /// holds it. That is the package's standing rule, and this is one of the two places it would otherwise be
    /// easiest to break.
    /// </para>
    /// <para>
    /// THE CONTEXT IS BORROWED AND THE BUFFER IS BORROWED. The device owns its immediate context and
    /// <see cref="D3D11Buffer"/> owns the buffer, so this type releases neither. Its whole state is two
    /// references and its whole behaviour is two calls.
    /// </para>
    /// <para>
    /// MAPPING IS A CONTEXT CALL, so it is serialised by the device's submit lock like every other one (decision
    /// W4, which puts staging <c>Map</c> and <c>Unmap</c> under it for the duration of the map call alone).
    /// <see cref="D3D11RingAllocator"/> takes that lock around both members here, so nothing in this type has to
    /// think about it.
    /// </para>
    /// </summary>
    internal sealed class D3D11BufferRingMemory : ID3D11RingMemory
    {
        readonly Vortice.Direct3D11.ID3D11DeviceContext _context;
        readonly Vortice.Direct3D11.ID3D11Buffer _buffer;

        /// <summary>Build the mapping mechanism for one ring-backed buffer. Neither argument is taken over.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal D3D11BufferRingMemory(
            Vortice.Direct3D11.ID3D11DeviceContext context, Vortice.Direct3D11.ID3D11Buffer buffer)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        }

        /// <inheritdoc/>
        public IntPtr MapWriteNoOverwrite()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) throw D3D11PlatformGuard.NotOnThisPlatform("uniform ring");

            return MapWindows();
        }

        /// <inheritdoc/>
        public void Unmap()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) throw D3D11PlatformGuard.NotOnThisPlatform("uniform ring");

            UnmapWindows();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        IntPtr MapWindows()
        {
            _context.Map(_buffer, 0, Vortice.Direct3D11.MapMode.WriteNoOverwrite,
                Vortice.Direct3D11.MapFlags.None, out Vortice.Direct3D11.MappedSubresource mapped);
            return mapped.DataPointer;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void UnmapWindows() => _context.Unmap(_buffer, 0);
    }
}
