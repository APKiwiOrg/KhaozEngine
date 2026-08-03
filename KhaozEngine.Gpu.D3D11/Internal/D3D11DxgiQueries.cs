using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// EVERY QUESTION DECISIONS G1 AND G2 ASK A REAL DEVICE OR A REAL ADAPTER, and nothing else. Six queries in
    /// total: the adapter name, three multisample checks, one format-support check, and the software-adapter flag,
    /// plus the adapter enumeration. Everything downstream of them (the fold, the constants, the trimming, the
    /// selection policy, the guard) is engine logic in <see cref="D3D11CapabilityRead"/> and
    /// <see cref="D3D11AdapterSelection"/>, where it runs on macOS.
    /// <para>
    /// Every body here is <see cref="MethodImplOptions.NoInlining"/> behind
    /// <see cref="KhaozEngineD3D11.IsPlatformSupported"/>, which is what keeps the JIT from resolving a Vortice
    /// type until such a body is first compiled, so the interop stays off the load path on macOS and Linux even
    /// though the package targets <c>net10.0</c> and ships there. There are no static fields of Vortice types, for
    /// the same reason: a static initializer would resolve them on first touch of ANY member.
    /// </para>
    /// <para>
    /// DEFENSIVE ON EVERY QUERY, and that matches the incumbent rather than being extra caution.
    /// <c>VeldridMap.MaxMsaaSampleCount</c> and <c>VeldridMap.SupportsShadowMaps</c> both swallow and degrade,
    /// because a capability read that throws would fail device creation over a question whose answer only decides
    /// how pretty the frame is. Section 11 says the same in as many words: any multisample query failure yields
    /// 1.
    /// </para>
    /// </summary>
    internal static class D3D11DxgiQueries
    {
        /// <summary>
        /// The whole capability set off a live device and the adapter it runs on, which is the single source both
        /// <c>GpuDeviceContext.Capabilities</c> and <c>IGpuDevice.Capabilities</c> read on this backend.
        /// </summary>
        /// <param name="device">The created device.</param>
        /// <param name="adapter">The adapter it was created on, which the device row already holds.</param>
        /// <param name="supportsCompletionFences"><see cref="D3D11FenceSubsystem.SupportsCompletionFences"/>, so
        /// the capability and the fence path cannot disagree.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static GpuCapabilities ReadCapabilitiesWindows(ID3D11Device device, IDXGIAdapter adapter,
            bool supportsCompletionFences)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(adapter);

            return D3D11CapabilityRead.Assemble(
                AdapterNameWindows(adapter),
                MaxMsaaSampleCountWindows(device),
                SupportsShadowMapsWindows(device),
                supportsCompletionFences);
        }

        /// <summary>The adapter description, exactly as DXGI gives it.
        /// <c>IDXGIAdapter::GetDesc().Description</c> through
        /// <see cref="D3D11CapabilityRead.TrimAdapterName"/>, which cuts at the first NUL and changes nothing
        /// else, so this is the same string the incumbent reports through <c>GraphicsDevice.DeviceName</c> (it
        /// assigns <c>desc.Description</c> raw) and the parity assertion on it holds by construction. Whitespace
        /// a vendor padded with is KEPT, for the reason recorded on
        /// <see cref="D3D11CapabilityRead.TrimAdapterName"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static string AdapterNameWindows(IDXGIAdapter adapter)
        {
            try
            {
                return D3D11CapabilityRead.TrimAdapterName(adapter.Description.Description);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Decision C4's answer: the MIN over the three formats the 3D scene's MRT renders into. Any query failure
        /// yields 1 for the WHOLE fold rather than for one format, because a device that will not answer one of
        /// the three has not told us the other two are usable together either.
        /// <para>
        /// THE DEPTH ATTACHMENT GOES IN AS <c>R32G8X24_TYPELESS</c>, not as the fully typed
        /// <c>D32_FLOAT_S8X24_UINT</c>, because the typeless sibling is the format the incumbent ACTUALLY hands
        /// <c>CheckMultisampleQualityLevels</c>. Its <c>GetSampleCountLimit(D32_Float_S8_UInt, depthFormat:
        /// true)</c> runs the pair through <c>D3D11Formats.ToDxgiFormat</c> first, and that mapping answers
        /// <c>Format.R32G8X24_Typeless</c> for a depth-flagged <c>D32_Float_S8_UInt</c>
        /// (<c>src/Veldrid/D3D11/D3D11Formats.cs</c> lines 131 to 133). Asking about the typed format here would
        /// be a DIFFERENT question, and a driver that answered the two differently would move
        /// <c>MaxMsaaSampleCount</c> off parity with nothing able to say why.
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static int MaxMsaaSampleCountWindows(ID3D11Device device)
        {
            try
            {
                return D3D11CapabilityRead.MinOverFormats(
                    HighestSampleCountWindows(device, Format.R8G8B8A8_UNorm),
                    HighestSampleCountWindows(device, Format.R32_Float),
                    HighestSampleCountWindows(device, Format.R32G8X24_Typeless));
            }
            catch
            {
                return D3D11CapabilityRead.NoMultisampling;
            }
        }

        /// <summary>
        /// Whether R32_FLOAT is usable as BOTH a render target and a sampled 2D texture, which is what the
        /// directional shadow map needs (it renders depth into an R32_FLOAT target and samples that target for the
        /// manual PCF compare).
        /// <para>
        /// TWO BITS, WHICH IS EXACTLY WHAT THE INCUMBENT ENDS UP CHECKING.
        /// <c>VeldridMap.SupportsShadowMaps</c> calls
        /// <c>GetPixelFormatSupport(R32_Float, Texture2D, RenderTarget | Sampled)</c>, and Veldrid's
        /// <c>D3D11GraphicsDevice.GetPixelFormatSupportCore</c> turns that USAGE pair into a
        /// <c>RenderTarget</c> test and a <c>ShaderSample</c> test against the result of
        /// <c>CheckFormatSupport</c>. The texture TYPE selects no bit at all there, so requiring
        /// <c>FormatSupport.Texture2D</c> as well would be a stricter question than the incumbent asks and could
        /// report false where the incumbent reports true, which is the shadow path silently degrading to blob
        /// shadows on one backend only.
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static bool SupportsShadowMapsWindows(ID3D11Device device)
        {
            try
            {
                const FormatSupport required = FormatSupport.RenderTarget | FormatSupport.ShaderSample;
                return (device.CheckFormatSupport(Format.R32_Float) & required) == required;
            }
            catch
            {
                // Degrade to blob shadows rather than throw, exactly as the incumbent does. A capability read is
                // never allowed to be the thing that fails device creation.
                return false;
            }
        }

        /// <summary>
        /// Decision G2's telemetry half, read off the CREATED DEVICE rather than off the choice, so it is right on
        /// every path including the default enumeration where nothing in the engine picked the adapter. Walks
        /// device to <c>IDXGIDevice</c> to its adapter to <c>IDXGIAdapter1</c> and reads
        /// <c>DXGI_ADAPTER_FLAG_SOFTWARE</c>.
        /// <para>
        /// The caller ORs this with "the selection asked for WARP" (<see cref="D3D11AdapterSelection.IsSoftwareChoice"/>).
        /// The flag is the authority whenever it is set, and the OR covers the one case it is documented not to
        /// be: a WARP device whose adapter does not carry the flag still ran on a software rasterizer, and a
        /// header field that said otherwise would misattribute every measurement in the capture.
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static bool IsSoftwareAdapterWindows(ID3D11Device device)
        {
            ArgumentNullException.ThrowIfNull(device);

            try
            {
                using IDXGIDevice? dxgi = device.QueryInterfaceOrNull<IDXGIDevice>();
                if (dxgi is null) return false;

                using IDXGIAdapter adapter = dxgi.GetAdapter();
                using IDXGIAdapter1? adapter1 = adapter.QueryInterfaceOrNull<IDXGIAdapter1>();
                if (adapter1 is null) return false;

                return (adapter1.Description1.Flags & AdapterFlags.Software) != 0;
            }
            catch
            {
                // A diagnostic flag, so an unanswerable query reports the non-alarming value rather than failing
                // anything. The session log's adapter line still names what ran.
                return false;
            }
        }

        /// <summary>
        /// Every adapter DXGI enumerates, in enumeration order, as the plain descriptions and flags
        /// <see cref="D3D11AdapterSelection.Choose"/> decides over. The <c>IDXGIAdapter1</c> objects are released
        /// here: the choice is an INDEX, and the device row re-enumerates to that index when it creates, which is
        /// what keeps this from handing out COM objects with an ownership question attached.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static IReadOnlyList<D3D11AdapterInfo> DescribeAdaptersWindows(IDXGIFactory1 factory)
        {
            ArgumentNullException.ThrowIfNull(factory);

            var adapters = new List<D3D11AdapterInfo>();
            try
            {
                for (int i = 0; ; i++)
                {
                    // A non-success result is the end of the enumeration (DXGI_ERROR_NOT_FOUND), and it is not a
                    // fault. A partial success would leak, so the adapter is disposed either way.
                    SharpGen.Runtime.Result result = factory.EnumAdapters1(i, out IDXGIAdapter1? adapter);
                    if (result.Failure || adapter is null)
                    {
                        adapter?.Dispose();
                        break;
                    }

                    try
                    {
                        AdapterDescription1 description = adapter.Description1;
                        adapters.Add(new D3D11AdapterInfo(
                            D3D11CapabilityRead.TrimAdapterName(description.Description),
                            (description.Flags & AdapterFlags.Software) != 0));
                    }
                    finally
                    {
                        adapter.Dispose();
                    }
                }
            }
            catch
            {
                // Whatever was enumerated before the failure is still a usable list, and an empty one is a
                // perfectly good input to the selection policy: every request then warns and falls back to
                // letting DXGI pick, which is the behaviour the engine had before this lever existed.
            }
            return adapters;
        }

        // One CheckMultisampleQualityLevels walk for one format. Separate so the delegate the device-free walk
        // takes is the only thing crossing the boundary, and so a failing format answers 1 for itself rather than
        // taking the other two with it.
        //
        // THE FORMAT TRAVELS THROUGH THE CLOSURE AS ITS ORDINAL, and that is the package's no-Vortice-value-type-
        // field rule reaching a place nothing else in it does. A lambda capturing the enum puts a Format FIELD on
        // the compiler-generated display class, and that class is a type in this assembly like any other, so a
        // reflection walk that loads every type in the package computes its layout and resolves the interop
        // assembly on macOS. There is exactly one such walk and it is deliberate:
        // D3D11ResourceModelTests.OffWindows_LoadingEveryTypeInTheBackend_PullsInNoInterop. Its sibling
        // D3D11DiagnosticsBoundaryTests explicitly forbids adding a second one, because what both assert is a
        // process-wide fact. The device travels as itself, because it is a reference and a reference field needs
        // no layout from the type it points at.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static int HighestSampleCountWindows(ID3D11Device device, Format format)
        {
            int formatOrdinal = (int)format;
            return D3D11CapabilityRead.HighestSupportedSampleCount(
                count => QualityLevelsWindows(device, (Format)formatOrdinal, count));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        static int QualityLevelsWindows(ID3D11Device device, Format format, int sampleCount)
        {
            try
            {
                return device.CheckMultisampleQualityLevels(format, sampleCount);
            }
            catch
            {
                return 0;
            }
        }
    }
}
