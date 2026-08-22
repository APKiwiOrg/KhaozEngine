using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// <see cref="IGpuTexture"/> for the native Direct3D 11 backend: one <c>ID3D11Texture2D</c> plus AT MOST FOUR
    /// eager views, decided from the declared usage by <see cref="D3D11ViewPolicy"/> and created here, once, at
    /// construction (decision X1).
    /// <para>
    /// THE FOUR, and what each covers: a shader resource view over the FULL mip chain and every array layer when
    /// the texture is sampled or generates mips, a render target view at mip 0 layer 0, a depth-stencil view at
    /// mip 0 layer 0, and an unordered access view at mip 0. The policy type carries the argument for why the seam
    /// cannot ask for a fifth, and it is a fact about the seam rather than an optimistic bound.
    /// </para>
    /// <para>
    /// THE RESOURCE IS TYPELESS, THE VIEWS ARE CONCRETE, exactly as the incumbent does it. That is what lets one
    /// depth texture carry both a depth-stencil view that writes it and a shader resource view that samples it,
    /// which the shadow path needs. <see cref="D3D11Formats"/> holds the three format readings.
    /// </para>
    /// <para>
    /// The engine's seam has 2D textures only, with an array-layer count and an optional cubemap bit, so there is
    /// no 1D or 3D path here at all. A texture is never both a cubemap and a storage image, which the unordered
    /// access branch states rather than silently mis-viewing.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11Texture : IGpuTexture, ID3D11BindableViews, ID3D11MappableResource
    {
        readonly DeviceLiveness _liveness;

        internal D3D11Texture(ID3D11Device device, DeviceLiveness liveness, in GpuTextureDescription description)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(liveness);

            _liveness = liveness;
            Width = description.Width;
            Height = description.Height;
            MipLevels = description.MipLevels == 0 ? 1 : description.MipLevels;
            ArrayLayers = description.ArrayLayers == 0 ? 1 : description.ArrayLayers;
            IsArray = description.IsArray;
            SampleCount = description.SampleCount;
            Format = description.Format;
            Usage = description.Usage;
            Views = D3D11ViewPolicy.ForTexture(description.Usage);
            IsCubemap = (description.Usage & GpuTextureUsage.Cubemap) != 0;
            ArraySlices = D3D11UploadBounds.ArraySlices(description);

            if (Views.UnorderedAccess && IsCubemap)
            {
                throw new ArgumentException(
                    "A cubemap cannot also be a storage image on Direct3D 11: an unordered access view has no "
                    + "cubemap dimension. Write into a 2D array and sample it as a cubemap instead.",
                    nameof(description));
            }

            IsDepthTarget = (description.Usage & GpuTextureUsage.DepthStencil) != 0;
            DeviceTexture = CreateTextureWindows(device, this);

            if (Views.ShaderResource) ShaderResourceView = CreateSrvWindows(device, this);
            if (Views.RenderTarget) RenderTargetView = CreateRtvWindows(device, this);
            if (Views.DepthStencil) DepthStencilView = CreateDsvWindows(device, this);
            if (Views.UnorderedAccess) UnorderedAccessView = CreateUavWindows(device, this);
        }

        /// <inheritdoc/>
        public uint Width { get; }
        /// <inheritdoc/>
        public uint Height { get; }
        /// <inheritdoc/>
        public uint MipLevels { get; }
        /// <inheritdoc/>
        public uint SampleCount { get; }
        /// <inheritdoc/>
        public GpuPixelFormat Format { get; }

        /// <summary>Array-layer count. For a cubemap this counts CUBES, and the resource carries six subresource
        /// slices per cube.</summary>
        internal uint ArrayLayers { get; }

        /// <summary>The REAL subresource slice count, six per logical layer on a cubemap. This is the
        /// <c>ArraySize</c> the resource is created with and the bound a subresource index is valid against, which
        /// is why the device-level upload checks the caller's array layer against it (#695).</summary>
        internal uint ArraySlices { get; }

        /// <summary>Whether the seam asked for an ARRAY, which the layer count alone cannot say at one layer
        /// (#666). It decides the SHADER-VISIBLE view dimensions only, so a one-layer array reaches an HLSL
        /// <c>Texture2DArray</c> / <c>RWTexture2DArray</c> as one. The render-target and depth-stencil views keep
        /// the layer-count rule: nothing declares their dimension in a shader, and their array arm already pins
        /// <c>ArraySize = 1</c>, so both arms name the same slice.</summary>
        internal bool IsArray { get; }

        /// <summary>The declared usage.</summary>
        internal GpuTextureUsage Usage { get; }
        /// <summary>Whether the texture is a cubemap.</summary>
        internal bool IsCubemap { get; }
        /// <summary>Whether the texture was declared as a depth-stencil attachment, which is the reading the
        /// format mapping takes.</summary>
        internal bool IsDepthTarget { get; }
        /// <summary>Which views this texture carries, decided by <see cref="D3D11ViewPolicy"/>.</summary>
        internal D3D11TextureViewPlan Views { get; }

        /// <summary>
        /// The concrete DXGI format the views read and write through. COMPUTED rather than stored, and that is
        /// not a style choice: a Vortice value-type FIELD anywhere in this package makes the CLR resolve the
        /// Vortice assembly the moment the declaring type is loaded, and the load-path guard asserts process-wide
        /// that nothing pulls the interop in off Windows. A reflection scan over the assembly's types is enough
        /// to trip it, and the suite has one. Fields of an interface type are fine, since a reference field needs
        /// no layout resolution. See this package's README.
        /// </summary>
        internal Format DxgiFormat => D3D11Formats.ToDxgiFormat(Format, IsDepthTarget);

        /// <summary>The typeless format the RESOURCE itself is created with. Computed, for the reason on
        /// <see cref="DxgiFormat"/>.</summary>
        internal Format TypelessFormat => D3D11Formats.ToTypelessFormat(DxgiFormat);

        /// <summary>The native texture.</summary>
        internal ID3D11Texture2D DeviceTexture { get; }
        /// <summary>Full-chain, all-layers shader resource view, or null.</summary>
        internal ID3D11ShaderResourceView? ShaderResourceView { get; }
        /// <summary>Mip 0, layer 0 render target view, or null.</summary>
        internal ID3D11RenderTargetView? RenderTargetView { get; }
        /// <summary>Mip 0, layer 0 depth-stencil view, or null.</summary>
        internal ID3D11DepthStencilView? DepthStencilView { get; }
        /// <summary>Mip 0 unordered access view, or null.</summary>
        internal ID3D11UnorderedAccessView? UnorderedAccessView { get; }

        /// <summary>True once disposed, whether or not anything native was released.</summary>
        internal bool IsDisposed { get; private set; }

        // ---- ID3D11BindableViews: a texture fills 't' when it is sampled and 'u' when it is storage ----
        //
        // Both are null unless the DECLARED usage earned the view at creation (decision X1), which is what turns
        // "bound at a register its usage never gave it a view for" into a named refusal at the bind rather than a
        // register silently holding nothing.

        /// <inheritdoc/>
        object? ID3D11BindableViews.ShaderResourceViewObject => ShaderResourceView;

        /// <inheritdoc/>
        object? ID3D11BindableViews.UnorderedAccessViewObject => UnorderedAccessView;

        /// <inheritdoc/>
        object? ID3D11BindableViews.SamplerStateObject => null;

        /// <inheritdoc/>
        object? ID3D11BindableViews.BufferObject => null;

        // ---- ID3D11MappableResource: what a staging Map needs, answered by the resource ----

        /// <inheritdoc/>
        object ID3D11MappableResource.MapTarget => DeviceTexture;

        /// <inheritdoc/>
        /// <remarks>Staging alone, unlike a buffer. A texture has no DYNAMIC arm on this seam and is never
        /// ring-backed, so the declared staging usage is the whole of what earns CPU access.</remarks>
        bool ID3D11MappableResource.IsMappable => Views.Staging;

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            if (_liveness.IsDead) return;   // the device already freed every child object

            ShaderResourceView?.Dispose();
            RenderTargetView?.Dispose();
            DepthStencilView?.Dispose();
            UnorderedAccessView?.Dispose();
            DeviceTexture.Dispose();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static ID3D11Texture2D CreateTextureWindows(ID3D11Device device, D3D11Texture t)
        {
            var d = new Texture2DDescription
            {
                Width = (int)t.Width,
                Height = (int)t.Height,
                MipLevels = (int)t.MipLevels,
                ArraySize = (int)t.ArraySlices,
                Format = t.TypelessFormat,
                BindFlags = D3D11Formats.ToBindFlags(t.Views.Bind),
                CPUAccessFlags = t.Views.Staging ? CpuAccessFlags.Read | CpuAccessFlags.Write : CpuAccessFlags.None,
                Usage = t.Views.Staging ? ResourceUsage.Staging : ResourceUsage.Default,
                SampleDescription = new SampleDescription((int)t.SampleCount, 0),
                MiscFlags = MiscFlagsFor(t),
            };
            return device.CreateTexture2D(d);
        }

        static ResourceOptionFlags MiscFlagsFor(D3D11Texture t)
        {
            ResourceOptionFlags flags = ResourceOptionFlags.None;
            if (t.IsCubemap) flags |= ResourceOptionFlags.TextureCube;
            if ((t.Usage & GpuTextureUsage.GenerateMipmaps) != 0) flags |= ResourceOptionFlags.GenerateMips;
            return flags;
        }

        // The FULL chain and every layer. There is no seam member that asks for a sub-range, so one view is the
        // whole requirement and a per-binding view would be the lazy shape X1 removes.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ID3D11ShaderResourceView CreateSrvWindows(ID3D11Device device, D3D11Texture t)
        {
            var d = new ShaderResourceViewDescription { Format = D3D11Formats.ToViewFormat(t.DxgiFormat) };

            if (t.IsCubemap && t.ArrayLayers == 1)
            {
                d.ViewDimension = ShaderResourceViewDimension.TextureCube;
                d.TextureCube.MostDetailedMip = 0;
                d.TextureCube.MipLevels = (int)t.MipLevels;
            }
            else if (t.IsCubemap)
            {
                d.ViewDimension = ShaderResourceViewDimension.TextureCubeArray;
                d.TextureCubeArray.MostDetailedMip = 0;
                d.TextureCubeArray.MipLevels = (int)t.MipLevels;
                d.TextureCubeArray.First2DArrayFace = 0;
                d.TextureCubeArray.NumCubes = (int)t.ArrayLayers;
            }
            else if (!t.IsArray)
            {
                d.ViewDimension = t.SampleCount > 1
                    ? ShaderResourceViewDimension.Texture2DMultisampled
                    : ShaderResourceViewDimension.Texture2D;
                d.Texture2D.MostDetailedMip = 0;
                d.Texture2D.MipLevels = (int)t.MipLevels;
            }
            else
            {
                // NO MULTISAMPLE ARM HERE, and none is needed: the multisample test sits inside the non-array arm
                // above, so an array that was also multisampled would take a plain Texture2DArray view over a
                // multisampled resource. GpuTextureDescription refuses that combination at description time
                // (RequireNoMultisampledArray), which is why this arm can assume single-sample.
                d.ViewDimension = ShaderResourceViewDimension.Texture2DArray;
                d.Texture2DArray.MostDetailedMip = 0;
                d.Texture2DArray.MipLevels = (int)t.MipLevels;
                d.Texture2DArray.FirstArraySlice = 0;
                d.Texture2DArray.ArraySize = (int)t.ArrayLayers;
            }

            return device.CreateShaderResourceView(t.DeviceTexture, d);
        }

        // Mip 0, layer 0. CreateFramebuffer takes bare textures with no mip and no layer parameter, so nothing can
        // ask for another slice.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ID3D11RenderTargetView CreateRtvWindows(ID3D11Device device, D3D11Texture t)
        {
            var d = new RenderTargetViewDescription { Format = D3D11Formats.ToDxgiFormat(t.Format, false) };

            if (t.ArrayLayers > 1 || t.IsCubemap)
            {
                if (t.SampleCount > 1)
                {
                    d.ViewDimension = RenderTargetViewDimension.Texture2DMultisampledArray;
                    d.Texture2DMSArray.FirstArraySlice = 0;
                    d.Texture2DMSArray.ArraySize = 1;
                }
                else
                {
                    d.ViewDimension = RenderTargetViewDimension.Texture2DArray;
                    d.Texture2DArray.MipSlice = 0;
                    d.Texture2DArray.FirstArraySlice = 0;
                    d.Texture2DArray.ArraySize = 1;
                }
            }
            else if (t.SampleCount > 1)
            {
                d.ViewDimension = RenderTargetViewDimension.Texture2DMultisampled;
            }
            else
            {
                d.ViewDimension = RenderTargetViewDimension.Texture2D;
                d.Texture2D.MipSlice = 0;
            }

            return device.CreateRenderTargetView(t.DeviceTexture, d);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static ID3D11DepthStencilView CreateDsvWindows(ID3D11Device device, D3D11Texture t)
        {
            var d = new DepthStencilViewDescription { Format = D3D11Formats.ToDepthViewFormat(t.Format) };

            if (t.ArrayLayers > 1 || t.IsCubemap)
            {
                if (t.SampleCount > 1)
                {
                    d.ViewDimension = DepthStencilViewDimension.Texture2DMultisampledArray;
                    d.Texture2DMSArray.FirstArraySlice = 0;
                    d.Texture2DMSArray.ArraySize = 1;
                }
                else
                {
                    d.ViewDimension = DepthStencilViewDimension.Texture2DArray;
                    d.Texture2DArray.MipSlice = 0;
                    d.Texture2DArray.FirstArraySlice = 0;
                    d.Texture2DArray.ArraySize = 1;
                }
            }
            else if (t.SampleCount > 1)
            {
                d.ViewDimension = DepthStencilViewDimension.Texture2DMultisampled;
            }
            else
            {
                d.ViewDimension = DepthStencilViewDimension.Texture2D;
                d.Texture2D.MipSlice = 0;
            }

            return device.CreateDepthStencilView(t.DeviceTexture, d);
        }

        // Mip 0, every layer. A compute kernel writes the base level and the graphics pass samples the chain.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static ID3D11UnorderedAccessView CreateUavWindows(ID3D11Device device, D3D11Texture t)
        {
            var d = new UnorderedAccessViewDescription { Format = D3D11Formats.ToViewFormat(t.DxgiFormat) };

            if (!t.IsArray)
            {
                d.ViewDimension = UnorderedAccessViewDimension.Texture2D;
                d.Texture2D.MipSlice = 0;
            }
            else
            {
                d.ViewDimension = UnorderedAccessViewDimension.Texture2DArray;
                d.Texture2DArray.MipSlice = 0;
                d.Texture2DArray.FirstArraySlice = 0;
                d.Texture2DArray.ArraySize = (int)t.ArrayLayers;
            }

            return device.CreateUnorderedAccessView(t.DeviceTexture, d);
        }
    }
}
