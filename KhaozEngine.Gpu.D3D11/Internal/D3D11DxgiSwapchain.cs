using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE SHIPPED <see cref="ID3D11SwapchainSurface"/>: an <c>IDXGISwapChain</c> and the four calls
    /// <see cref="D3D11Swapchain"/> makes on it. Everything that decides WHEN those calls happen is on the engine
    /// side, which is the entire point of the interface this implements.
    /// <para>
    /// DECISION W1 LIVES IN THE DESCRIPTION BELOW, field for field, and it is a REPRODUCTION of the incumbent
    /// rather than a design: unversioned <c>IDXGIFactory</c> off the adapter, <c>BufferCount = 2</c>,
    /// <c>Windowed = true</c>, <c>SwapEffect.Discard</c> (the legacy BLIT model), <c>SampleDescription(1, 0)</c>,
    /// <c>B8G8R8A8_UNorm</c> and <c>Usage.RenderTargetOutput</c>, plus the
    /// <c>MakeWindowAssociation(IgnoreAltEnter)</c> that stops DXGI toggling fullscreen behind the windowing
    /// layer's back. Do not modernise any of it here. The flip model, <c>ALLOW_TEARING</c>, the
    /// RTV-unbound-at-present obligation that comes with it, a waitable frame-latency object and the #380 pacing
    /// work are ONE sequenced follow-up with their own manual validation, because the swapchain is the one area of
    /// this backend that no automated test anywhere can see: the goldens are headless, the shape tests are
    /// device-free, and the WARP leg never presents.
    /// </para>
    /// <para>
    /// THE COLOUR FORMAT IS NON-sRGB, which is the incumbent's <c>ColorSrgb: false</c> reaching this decision from
    /// <c>GpuDeviceContext</c>'s windowed device options. It is load-bearing for every committed golden, since the
    /// sRGB variant would gamma-encode on write and shift every pixel the renderers produce.
    /// </para>
    /// <para>
    /// THE DEPTH ATTACHMENT IS OPTIONAL AND IS NULL ON EVERY SHIPPED PATH. The incumbent's swapchain builds a
    /// depth texture when its description carries a depth format, and the engine's windowed creation passes none
    /// (<c>new SwapchainDescription(source, width, height, null, syncToVerticalBlank, false)</c>), so the renderers
    /// bring their own depth targets. The path is reproduced anyway, because a swapchain type that cannot express
    /// it would be a silent behaviour change the first time a consumer asked.
    /// </para>
    /// <para>
    /// Every body that names a Vortice type is <see cref="MethodImplOptions.NoInlining"/> behind
    /// <see cref="KhaozEngineD3D11.IsPlatformSupported"/>, so the interop stays off the load path on macOS and
    /// Linux even though this type ships there. Its fields are all REFERENCE fields for the same reason: a Vortice
    /// value-type field would make the CLR resolve the interop merely to compute this type's layout, and the
    /// suite's load-path assertions call <c>Assembly.GetTypes</c>, which forces exactly that.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11DxgiSwapchain : ID3D11SwapchainSurface
    {
        readonly ID3D11Device _device;
        readonly ID3D11DeviceContext _context;
        readonly IDXGISwapChain _swapchain;
        readonly D3D11DeviceLiveness _liveness;

        ID3D11RenderTargetView? _renderTargetView;
        D3D11Texture? _depthTexture;
        bool _disposed;

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        D3D11DxgiSwapchain(ID3D11Device device, ID3D11DeviceContext immediateContext, IDXGISwapChain swapchain,
            GpuPixelFormat? depthFormat, D3D11DeviceLiveness liveness)
        {
            _device = device;
            _context = immediateContext;
            _swapchain = swapchain;
            _liveness = liveness;
            DepthFormat = depthFormat;
        }

        /// <summary>
        /// Create the blit-model swapchain for <paramref name="hwnd"/>, reproducing the incumbent's description
        /// exactly (decision W1). No attachments are created here: <see cref="D3D11Swapchain"/> asks for the first
        /// generation, so the one place attachments come from is the one place they are published.
        /// </summary>
        /// <param name="device">The device the swapchain presents from. Borrowed, never released here.</param>
        /// <param name="immediateContext">The device's immediate context, borrowed the same way, and needed for
        /// one reason only: <see cref="ReleaseAttachments"/> has to unbind the backbuffer's views from it before
        /// they can be released, since the context's own reference is one <c>ResizeBuffers</c> fails on.</param>
        /// <param name="adapter">The adapter the factory is fetched off, which is how the incumbent reaches an
        /// <c>IDXGIFactory</c> and is the adapter the explicit-selection work of decision G2 will own.</param>
        /// <param name="hwnd">The Win32 window handle to present into.</param>
        /// <param name="width">The initial backbuffer width.</param>
        /// <param name="height">The initial backbuffer height.</param>
        /// <param name="depthFormat">The depth attachment format, or null for none, which is what the engine's
        /// own windowed path passes.</param>
        /// <param name="liveness">The device's liveness token, so a release after device death is a no-op.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static D3D11DxgiSwapchain CreateWindows(ID3D11Device device, ID3D11DeviceContext immediateContext,
            IDXGIAdapter adapter, IntPtr hwnd, uint width, uint height, GpuPixelFormat? depthFormat,
            D3D11DeviceLiveness liveness)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(immediateContext);
            ArgumentNullException.ThrowIfNull(adapter);
            ArgumentNullException.ThrowIfNull(liveness);

            var description = new SwapChainDescription
            {
                BufferCount = BufferCount,
                Windowed = true,
                BufferDescription = new ModeDescription((int)width, (int)height, ColourDxgiFormat),
                OutputWindow = hwnd,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.Discard,
                BufferUsage = Usage.RenderTargetOutput,
            };

            using IDXGIFactory factory = adapter.GetParent<IDXGIFactory>();
            IDXGISwapChain swapchain = factory.CreateSwapChain(device, description);
            // Without this DXGI installs its own Alt-Enter handler and flips the swapchain to fullscreen behind
            // the windowing layer, which then holds a window mode that no longer matches reality. The incumbent
            // does the same, on the same flag.
            factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

            return new D3D11DxgiSwapchain(device, immediateContext, swapchain, depthFormat, liveness);
        }

        /// <inheritdoc/>
        public GpuPixelFormat ColourFormat => GpuPixelFormat.B8G8R8A8UNorm;

        /// <inheritdoc/>
        public GpuPixelFormat? DepthFormat { get; }

        /// <inheritdoc/>
        public void ReleaseAttachments()
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) throw D3D11PlatformGuard.NotOnThisPlatform("swapchain");

            ReleaseAttachmentsWindows();
        }

        /// <inheritdoc/>
        public void ResizeBuffers(uint width, uint height)
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) throw D3D11PlatformGuard.NotOnThisPlatform("swapchain");

            ResizeBuffersWindows(width, height);
        }

        /// <inheritdoc/>
        public D3D11SwapchainAttachments CreateAttachments(uint width, uint height)
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) throw D3D11PlatformGuard.NotOnThisPlatform("swapchain");

            return CreateAttachmentsWindows(width, height);
        }

        /// <inheritdoc/>
        public int Present(int syncInterval)
        {
            if (!KhaozEngineD3D11.IsPlatformSupported) throw D3D11PlatformGuard.NotOnThisPlatform("swapchain");

            return PresentWindows(syncInterval);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!KhaozEngineD3D11.IsPlatformSupported) return;

            DisposeWindows();
        }

        // The incumbent's two release points, in one place, plus the unbind the incumbent never has to make. The
        // depth texture releases its own views, and the liveness gate is what makes a teardown that runs after the
        // device was destroyed a no-op rather than a release against freed memory. The unbind rides the same gate,
        // for the same reason: a call on the immediate context of a destroyed device is a call against freed
        // memory too.
        //
        // THE UNBIND IS THE HALF THAT IS EASY TO MISS. ResizeBuffers fails on any surviving reference to a
        // backbuffer, INDIRECT ones included, and the immediate context holds one: OMSetRenderTargets AddRefs the
        // render target view and does not drop it because the application disposed its wrapper. The incumbent is
        // immune to this without doing anything about it, since it executes every command list with
        // restoreContextState false (D3D11GraphicsDevice.SubmitCommandsCore), which resets the context state at the
        // end of every submit. This backend deliberately does not: decision R3 puts its one ClearState at the HEAD
        // of a replay and the end of a submit emits nothing, so after the last submit of a frame the swapchain's
        // view is still bound in the output-merger when the resize applies at the present boundary. Without this
        // call the first real window resize throws DXGI_ERROR_INVALID_CALL out of Present, under the submit lock,
        // with the views already released.
        //
        // ZERO RENDER TARGETS IS THE NARROWEST CALL THAT PROVABLY DROPS IT. The output-merger is the only stage
        // that can be holding either of these views (the backbuffer is created RenderTargetOutput and is never a
        // shader resource here, and the depth texture is the swapchain's own), and one OMSetRenderTargets with a
        // count of zero and the default null depth view unbinds every render target AND the depth-stencil view,
        // which is exactly the set released below. ClearState would also work and is rejected as far wider than the
        // obligation: it would drop every shader, buffer, sampler and viewport as well.
        //
        // THE MANAGED CACHE DOES NOT SEE THIS, and that is fine rather than merely tolerated. D3D11DeviceState
        // still believes the framebuffer is bound, but a resize only ever lands at a present boundary (W3) and
        // R3's single ClearState at the head of the next replay resets the context and the cache together, so
        // nothing binds in between and the first SetFramebuffer afterwards is a change either way.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void ReleaseAttachmentsWindows()
        {
            if (_liveness.IsAlive)
            {
                _context.OMSetRenderTargets(0, Array.Empty<ID3D11RenderTargetView>());
                _renderTargetView?.Dispose();
            }

            _renderTargetView = null;

            // D3D11Texture reads the same token itself, so this is safe either way.
            _depthTexture?.Dispose();
            _depthTexture = null;
        }

        // Buffer count, format and flags exactly as created, which is what keeps the resize a resize rather than a
        // quiet reconfiguration. CheckError because a failed ResizeBuffers leaves the window presenting buffers
        // that no longer match it, and the usual cause is a surviving reference to a backbuffer, which is a defect
        // in the caller's ordering rather than a condition to carry on through.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void ResizeBuffersWindows(uint width, uint height)
            => _swapchain.ResizeBuffers(BufferCount, (int)width, (int)height, ColourDxgiFormat, SwapChainFlags.None)
                .CheckError();

        // The size comes off the RESOURCE rather than off the request, matching the incumbent, whose backbuffer
        // wrapper reads its own description. DXGI reads a zero dimension in ResizeBuffers as "match the window's
        // client area", and the Silk framebuffer-resize callback does forward a minimised window as 0 by 0, so the
        // requested size and the real one genuinely differ. The depth texture is then built at the REAL size,
        // because Direct3D 11 requires a depth-stencil view and a render target view bound together to have
        // matching dimensions, and the incumbent's use of the requested size for the depth attachment is a latent
        // mismatch that only its null depth format keeps unreachable.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        D3D11SwapchainAttachments CreateAttachmentsWindows(uint width, uint height)
        {
            using ID3D11Texture2D backbuffer = _swapchain.GetBuffer<ID3D11Texture2D>(0);
            Texture2DDescription description = backbuffer.Description;
            uint actualWidth = description.Width > 0 ? (uint)description.Width : width;
            uint actualHeight = description.Height > 0 ? (uint)description.Height : height;

            // Explicit rather than a null description, so the view this backend binds is stated here instead of
            // inferred from the resource. Mip 0 of a single-sample, single-layer backbuffer is the whole of it.
            var viewDescription = new RenderTargetViewDescription
            {
                Format = ColourDxgiFormat,
                ViewDimension = RenderTargetViewDimension.Texture2D,
            };
            viewDescription.Texture2D.MipSlice = 0;

            _renderTargetView = _device.CreateRenderTargetView(backbuffer, viewDescription);

            object? depthStencilView = null;
            if (DepthFormat is GpuPixelFormat depthFormat)
            {
                _depthTexture = new D3D11Texture(_device, _liveness, new GpuTextureDescription(
                    actualWidth, actualHeight, depthFormat, GpuTextureUsage.DepthStencil));
                depthStencilView = _depthTexture.DepthStencilView;
            }

            return new D3D11SwapchainAttachments(
                actualWidth, actualHeight, _renderTargetView, depthStencilView);
        }

        // The incumbent's present, argument for argument: the sync interval the vsync flag selects, and no flags.
        // The HRESULT is RETURNED rather than checked, which is the one departure and the seam decision G3's
        // device-loss latch needs: the incumbent discards it, and a discarded device removal surfaces frames later
        // as an unrelated crash.
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        int PresentWindows(int syncInterval) => _swapchain.Present(syncInterval, PresentFlags.None).Code;

        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        void DisposeWindows()
        {
            ReleaseAttachmentsWindows();
            if (_liveness.IsAlive) _swapchain.Dispose();
        }

        // Two buffers, which with SwapEffect.Discard is the legacy blit model's pair. Named once so the creation
        // description and every ResizeBuffers cannot drift: DXGI requires the resize to keep the count it was
        // created with unless it is asked to change it deliberately.
        const int BufferCount = 2;

        // B8G8R8A8_UNorm, non-sRGB. The engine reading is ColourFormat and this is the same format in DXGI terms,
        // named once so the swapchain description, the resize and the render target view all state one answer.
        //
        // A PROPERTY RATHER THAN A CONST, for the reason D3D11Texture.DxgiFormat gives at length. A const of a
        // Vortice enum is a static literal whose type the loader may resolve while laying out the static area,
        // which would put the interop on the load path of every macOS and Linux test run, and the suite's
        // load-path assertions call Assembly.GetTypes and would trip on it. A property has no field at all, and
        // its body is only ever JIT-compiled from a guarded Windows-only caller.
        static Format ColourDxgiFormat => Format.B8G8R8A8_UNorm;
    }
}
