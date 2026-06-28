# Terrain PBR Splat-Textured Materials Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the terrain's height/slope vertex-colour ramp with PBR splat-textured materials (grass/dirt/rock/sand/snow) blended per-fragment by the baked splat weights, with triplanar world-space tiling and normal maps, opt-in and byte-identical when no material is supplied.

**Architecture:** A dedicated Render3D "splat" pipeline (sibling of the model pipeline inside `ModelRenderer`, sharing the frame UBO + instance buffer) draws terrain meshes that carry the five splat weights packed into the existing `ModelVertex.Color` (4 channels + a 5th derived as `1 - sum`). Two `texture2DArray`s (albedo + normal, 5 layers each) plus a per-layer-scalar-roughness UBO feed a triplanar fragment shader. `Terrain.Render3D` maps the five named terrain layers onto the generic Render3D splat material; with no material supplied the existing ramp path is unchanged.

**Tech Stack:** C# net10.0, Veldrid behind `KhaozEngine.Gpu`, author-once GLSL `#version 450` cross-compiled via Veldrid.SPIRV, xUnit (`KhaozEngine.Tests`), `GpuFact` for on-device tests.

## Global Constraints

- net10.0, MonoGame-free. No new heavy native dependencies.
- Shaders authored once in GLSL `#version 450` in `KhaozEngine.Render3D/Internal/ShaderSources.cs`; must run on Metal / D3D11 / Vulkan. Sample textures in binding order (Metal SPIRV-Cross first-sample-order constraint).
- No em-dashes in any code comment, doc, commit message, or CHANGELOG entry (use periods/commas/parentheses).
- New behaviour ships with a headless test in `KhaozEngine.Tests` where it is pure (packing, config, params, UV math); device behaviour is a `GpuFact`; renderer visuals are verified by running the consumer sample.
- One shared `<KhaozEngineVersion>` line in `Directory.Build.props` governs all packages. The release ritual (bump + CHANGELOG + doc sweep + pack + tag) is a single batched task at the end; the push/tag is HELD and confirmed with the user.
- The five splat channels are fixed and ordered grass / dirt / rock / sand / snow (matches `TerrainSplatWeights`). Which texture fills each channel is the consumer's choice.
- Build: `dotnet build KhaozEngine.sln -c Debug`. Test: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`. `local-feed/` must exist before restore (`mkdir -p local-feed`).

---

### Task 1: GPU seam — anisotropic sampler support

**Files:**
- Modify: `KhaozEngine.Gpu/GpuEnums.cs` (add `GpuSamplerFilter.Anisotropic`)
- Modify: `KhaozEngine.Gpu/GpuDescriptions.cs` (add `MaximumAnisotropy` to `GpuSamplerDescription`)
- Modify: `KhaozEngine.Gpu/Internal/VeldridMap.cs` (map the new filter)
- Modify: `KhaozEngine.Gpu/Internal/VeldridGpuDevice.cs:135-141` (`CreateSampler`: pass anisotropy + device-feature fallback)
- Test: `KhaozEngine.Tests/Gpu/VeldridMapTests.cs` (create if absent) and `KhaozEngine.Tests/Gpu/AnisotropicSamplerGpuTests.cs` (create)

**Interfaces:**
- Produces: `GpuSamplerFilter.Anisotropic`; `GpuSamplerDescription(GpuSamplerFilter filter, GpuSamplerAddress addressU, addressV, addressW, uint maximumAnisotropy)` (new 5-arg ctor overload, existing ctor preserved with `maximumAnisotropy = 0`).

- [ ] **Step 1: Write the failing test** — pure enum-mapping test that `Anisotropic` maps to Veldrid's `SamplerFilter.Anisotropic`.

Append to `KhaozEngine.Tests/Gpu/VeldridMapTests.cs` (create the file with this content if it does not exist):

```csharp
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Veldrid;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public class VeldridMapTests
    {
        [Fact]
        public void AnisotropicFilterMapsToVeldrid()
        {
            Assert.Equal(SamplerFilter.Anisotropic, VeldridMap.ToVeldrid(GpuSamplerFilter.Anisotropic));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter AnisotropicFilterMapsToVeldrid`
Expected: FAIL to compile ("'GpuSamplerFilter' does not contain a definition for 'Anisotropic'").

- [ ] **Step 3: Add the enum value.** In `KhaozEngine.Gpu/GpuEnums.cs`, inside `enum GpuSamplerFilter` (after `MinLinearMagLinearMipLinear`):

```csharp
        /// <summary>Anisotropic filtering (grazing-angle quality for tiled ground). Requires device support;
        /// the impl falls back to <see cref="MinLinearMagLinearMipLinear"/> when the backend lacks it.</summary>
        Anisotropic,
```

- [ ] **Step 4: Map it.** In `KhaozEngine.Gpu/Internal/VeldridMap.cs`, in `ToVeldrid(GpuSamplerFilter f)` (before the `_ =>` arm):

```csharp
            GpuSamplerFilter.Anisotropic => SamplerFilter.Anisotropic,
```

- [ ] **Step 5: Add `MaximumAnisotropy` to the description.** In `KhaozEngine.Gpu/GpuDescriptions.cs`, replace the `GpuSamplerDescription` struct body's field list and ctor with:

```csharp
        /// <summary>Min/mag/mip filtering.</summary>
        public GpuSamplerFilter Filter { get; }
        /// <summary>Max anisotropy when <see cref="Filter"/> is <see cref="GpuSamplerFilter.Anisotropic"/>
        /// (ignored otherwise). 0 keeps the historical behaviour.</summary>
        public uint MaximumAnisotropy { get; }

        public GpuSamplerDescription(GpuSamplerFilter filter,
            GpuSamplerAddress addressU = GpuSamplerAddress.Clamp,
            GpuSamplerAddress addressV = GpuSamplerAddress.Clamp,
            GpuSamplerAddress addressW = GpuSamplerAddress.Clamp,
            uint maximumAnisotropy = 0)
        {
            Filter = filter; AddressModeU = addressU; AddressModeV = addressV; AddressModeW = addressW;
            MaximumAnisotropy = maximumAnisotropy;
        }
```

- [ ] **Step 6: Apply anisotropy + fallback in `CreateSampler`.** In `KhaozEngine.Gpu/Internal/VeldridGpuDevice.cs`, replace the `CreateSampler` method body (lines 135-141) with:

```csharp
        public IGpuSampler CreateSampler(in GpuSamplerDescription d)
        {
            // Anisotropic requires device support; fall back to trilinear so the splat-terrain sampler still
            // runs on a backend that lacks it (the path degrades, it does not break).
            var filter = d.Filter;
            uint maxAniso = d.MaximumAnisotropy;
            if (filter == GpuSamplerFilter.Anisotropic && !GraphicsDevice.Features.SamplerAnisotropy)
            {
                filter = GpuSamplerFilter.MinLinearMagLinearMipLinear;
                maxAniso = 0;
            }
            var desc = new SamplerDescription(
                VeldridMap.ToVeldrid(d.AddressModeU), VeldridMap.ToVeldrid(d.AddressModeV), VeldridMap.ToVeldrid(d.AddressModeW),
                VeldridMap.ToVeldrid(filter), null, maxAniso, 0, uint.MaxValue, 0, SamplerBorderColor.TransparentBlack);
            return new VeldridGpuSampler(GraphicsDevice.ResourceFactory.CreateSampler(desc));
        }
```

- [ ] **Step 7: Add a device test that an anisotropic sampler is created without throwing.** Create `KhaozEngine.Tests/Gpu/AnisotropicSamplerGpuTests.cs` (the `GpuDeviceContext.CreateHeadless()` pattern, matching `RenderScaleGpuTests`):

```csharp
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class AnisotropicSamplerGpuTests
    {
        [GpuFact]
        public void AnisotropicSamplerCreatesOrFallsBack()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            using var sampler = gpu.GpuDevice.Factory.CreateSampler(new GpuSamplerDescription(
                GpuSamplerFilter.Anisotropic, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, maximumAnisotropy: 8));
            Assert.NotNull(sampler);
        }
    }
}
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "AnisotropicFilterMapsToVeldrid|AnisotropicSamplerCreatesOrFallsBack"`
Expected: PASS (the GpuFact is skipped where no device is available, runs on Metal locally).

- [ ] **Step 9: Commit**

```bash
git add KhaozEngine.Gpu KhaozEngine.Tests/Gpu
git commit -m "gpu(terrain-pbr-splat): anisotropic sampler seam + device-feature fallback"
```

---

### Task 2: GPU seam — array-layer + mip texture upload and mip generation

**Files:**
- Modify: `KhaozEngine.Gpu/GpuDescriptions.cs` (add `GpuTextureDescription.Texture2DArray`)
- Modify: `KhaozEngine.Gpu/GpuInterfaces.cs` (add `UpdateTexture` layer/mip overload to `IGpuDevice`; add `GenerateMipmaps` to `IGpuCommandList`)
- Modify: `KhaozEngine.Gpu/Internal/VeldridGpuDevice.cs` (implement the `UpdateTexture` overload)
- Modify: `KhaozEngine.Gpu/Internal/VeldridGpuCommandList.cs` (implement `GenerateMipmaps`)
- Test: `KhaozEngine.Tests/Gpu/TextureArrayGpuTests.cs` (create)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces:
  - `GpuTextureDescription.Texture2DArray(uint width, uint height, GpuPixelFormat format, GpuTextureUsage usage, uint arrayLayers, uint mipLevels)`
  - `IGpuDevice.UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height, uint mipLevel, uint arrayLayer)` (the existing 6-arg overload stays)
  - `IGpuCommandList.GenerateMipmaps(IGpuTexture texture)`

- [ ] **Step 1: Write the failing test** — create a 5-layer array texture, upload each layer's base mip via the new overload, generate both mip chains, all on a live device without throwing. (A per-layer pixel readback needs backend-specific subresource offsets and is brittle; the visual correctness is verified by the sample. This test exercises the three new seam methods on a real device.)

Create `KhaozEngine.Tests/Gpu/TextureArrayGpuTests.cs`:

```csharp
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class TextureArrayGpuTests
    {
        [GpuFact]
        public void ArrayLayerUploadAndMipGenerationSucceed()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            const uint W = 4, H = 4, layers = 5, mips = 3; // floor(log2(4)) + 1 == 3
            using var tex = dev.Factory.CreateTexture(GpuTextureDescription.Texture2DArray(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps, layers, mips));

            for (uint L = 0; L < layers; L++)
            {
                var px = new byte[W * H * 4];
                for (int p = 0; p < px.Length; p += 4) { px[p] = (byte)(L * 40); px[p + 3] = 255; }
                dev.UpdateTexture(tex, px, 0, 0, W, H, mipLevel: 0, arrayLayer: L);
            }

            using IGpuCommandList cl = dev.Factory.CreateCommandList();
            cl.Begin();
            cl.GenerateMipmaps(tex);
            cl.End();
            dev.Submit(cl);
            dev.WaitForIdle();

            Assert.Equal(W, tex.Width);
            Assert.Equal(H, tex.Height);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter ArrayLayerUploadAndMipGenerationSucceed`
Expected: FAIL to compile (`Texture2DArray`, the 8-arg `UpdateTexture`, and `GenerateMipmaps` do not exist yet).

- [ ] **Step 3: Add the array texture-description helper.** In `KhaozEngine.Gpu/GpuDescriptions.cs`, after the existing `Texture2D` static method in `GpuTextureDescription`:

```csharp
        /// <summary>Convenience for a 2D texture ARRAY with explicit layer + mip counts (the splat-terrain layer
        /// stacks). The ctor already carries <see cref="MipLevels"/>/<see cref="ArrayLayers"/>; this names the
        /// array case.</summary>
        public static GpuTextureDescription Texture2DArray(uint width, uint height, GpuPixelFormat format,
            GpuTextureUsage usage, uint arrayLayers, uint mipLevels)
            => new(width, height, format, usage, mipLevels, arrayLayers);
```

- [ ] **Step 4: Declare the `UpdateTexture` overload + `GenerateMipmaps`.** In `KhaozEngine.Gpu/GpuInterfaces.cs`:

In `interface IGpuDevice`, after the existing `UpdateTexture` (line 177):

```csharp
        /// <summary>Upload CPU bytes into a texture sub-region at an explicit <paramref name="mipLevel"/> and
        /// <paramref name="arrayLayer"/> (the splat-terrain layer stacks upload each layer's base mip).</summary>
        void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height, uint mipLevel, uint arrayLayer);
```

In `interface IGpuCommandList`, after `CopyTexture` (line 143):

```csharp
        /// <summary>Generate the full mip chain of <paramref name="texture"/> from its base level. The texture must
        /// be created with <see cref="GpuTextureUsage.GenerateMipmaps"/> and a mip count &gt; 1.</summary>
        void GenerateMipmaps(IGpuTexture texture);
```

- [ ] **Step 5: Implement the device `UpdateTexture` overload.** In `KhaozEngine.Gpu/Internal/VeldridGpuDevice.cs`, after the existing `UpdateTexture` (line 58-59):

```csharp
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height, uint mipLevel, uint arrayLayer)
            => GraphicsDevice.UpdateTexture(((VeldridGpuTexture)texture).Texture, data, x, y, 0, width, height, 1, mipLevel, arrayLayer);
```

- [ ] **Step 6: Implement `GenerateMipmaps` on the command list.** In `KhaozEngine.Gpu/Internal/VeldridGpuCommandList.cs`, after the `CopyTexture` method (line 61-62):

```csharp
        public void GenerateMipmaps(IGpuTexture texture)
            => CommandList.GenerateMipmaps(((VeldridGpuTexture)texture).Texture);
```

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter ArrayLayerUploadAndMipGenerationSucceed`
Expected: PASS on a device (skipped where none).

- [ ] **Step 8: Commit**

```bash
git add KhaozEngine.Gpu KhaozEngine.Tests/Gpu
git commit -m "gpu(terrain-pbr-splat): array-layer + mip texture upload + GenerateMipmaps"
```

---

### Task 3: Render3D splat-material core (pure CPU logic)

**Files:**
- Create: `KhaozEngine.Render3D/Rendering/SplatMaterialConfig.cs` (enum, constants, params struct + builder, mip-count, layer image type)
- Create: `KhaozEngine.Render3D/Rendering/SplatMath.cs` (triplanar/planar blend + weight reconstruction; CPU mirror of the shader)
- Test: `KhaozEngine.Tests/Render3D/SplatMaterialTests.cs` (create)

**Interfaces:**
- Produces (all in namespace `KhaozEngine.Render3D`):
  - `enum SplatProjection { Triplanar = 0, PlanarXz = 1 }`
  - `sealed class SplatLayerImage { byte[] AlbedoRgba; byte[] NormalRgba; Color Tint = white; float TilesPerMetre = 0.25f; float Roughness = 0.85f; }`
  - `static class SplatMaterialConfig { const int LayerCount = 5; static uint MipLevelCount(int w, int h); static SplatParamsData BuildParams(IReadOnlyList<SplatLayerImage> layers, float triplanarSharpness, SplatProjection projection, float baseSpecStrength); }`
  - `struct SplatParamsData { const uint SizeInBytes = 112; }` (std140-aligned)
  - `static class SplatMath { static Vector3 TriplanarBlend(Vector3 normal, float sharpness); static Vector3 PlanarBlend(); static (float g,float d,float r,float s,float snow) UnpackWeights(Vector4 packed); }`

- [ ] **Step 1: Write the failing tests.** Create `KhaozEngine.Tests/Render3D/SplatMaterialTests.cs`:

```csharp
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SplatMaterialTests
    {
        [Fact]
        public void MipLevelCountIsFullChain()
        {
            Assert.Equal(1u, SplatMaterialConfig.MipLevelCount(1, 1));
            Assert.Equal(3u, SplatMaterialConfig.MipLevelCount(4, 4));
            Assert.Equal(9u, SplatMaterialConfig.MipLevelCount(256, 256));
            Assert.Equal(9u, SplatMaterialConfig.MipLevelCount(256, 128)); // max dimension drives it
        }

        [Fact]
        public void TriplanarBlendSumsToOneAndPicksDominantAxis()
        {
            var up = SplatMath.TriplanarBlend(new Vector3(0, 1, 0), 4f);
            Assert.True(up.Y > 0.99f);
            Assert.Equal(1f, up.X + up.Y + up.Z, 3);

            var side = SplatMath.TriplanarBlend(new Vector3(1, 0, 0), 4f);
            Assert.True(side.X > 0.99f);
        }

        [Fact]
        public void PlanarBlendIsXzOnly()
        {
            Assert.Equal(new Vector3(0, 1, 0), SplatMath.PlanarBlend());
        }

        [Fact]
        public void UnpackWeightsReconstructsFifthAndNormalizes()
        {
            // grass .4 dirt .1 rock .2 sand .1 -> snow .2, already normalized.
            var (g, d, r, s, snow) = SplatMath.UnpackWeights(new Vector4(0.4f, 0.1f, 0.2f, 0.1f));
            Assert.Equal(0.2f, snow, 4);
            Assert.Equal(1f, g + d + r + s + snow, 4);
        }

        [Fact]
        public void BuildParamsPacksPerLayerScalarsAndGlobals()
        {
            var layers = new List<SplatLayerImage>();
            for (int i = 0; i < 5; i++)
                layers.Add(new SplatLayerImage { Tint = Color.White, TilesPerMetre = 0.1f * (i + 1), Roughness = 0.1f * i });
            var p = SplatMaterialConfig.BuildParams(layers, triplanarSharpness: 6f, projection: SplatProjection.PlanarXz, baseSpecStrength: 0.2f);

            Assert.Equal(0.1f, p.TintTiling0.W, 4);          // layer 0 tiling
            Assert.Equal(0.5f, p.TintTiling4.W, 4);          // layer 4 tiling
            Assert.Equal(0.0f, p.Roughness.X, 4);            // layer 0 roughness
            Assert.Equal(0.3f, p.Roughness.W, 4);            // layer 3 roughness
            Assert.Equal(0.4f, p.Misc.X, 4);                 // layer 4 roughness
            Assert.Equal(6f, p.Misc.Y, 4);                   // triplanar sharpness
            Assert.Equal(1f, p.Misc.Z, 4);                   // PlanarXz == 1
            Assert.Equal(0.2f, p.Misc.W, 4);                 // base spec
        }

        [Fact]
        public void ParamsDataIs112Bytes()
        {
            Assert.Equal(112, (int)SplatParamsData.SizeInBytes);
            Assert.Equal(112, System.Runtime.InteropServices.Marshal.SizeOf<SplatParamsData>());
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter SplatMaterialTests`
Expected: FAIL to compile (types undefined).

- [ ] **Step 3: Create `SplatMaterialConfig.cs`.** Create `KhaozEngine.Render3D/Rendering/SplatMaterialConfig.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>How tiled detail textures are projected onto a splat-terrain surface. Triplanar blends three
    /// world-plane projections by the surface normal (no per-vertex tangent needed, no cliff smear); PlanarXz
    /// projects straight down (cheaper, smears steep faces) as a perf escape hatch.</summary>
    public enum SplatProjection { Triplanar = 0, PlanarXz = 1 }

    /// <summary>One layer of a splat material: a tileable albedo + tangent-space normal (RGBA8, same WxH as every
    /// other layer in the stack), a tint, the tiling rate (texture tiles per world metre), and a scalar roughness.
    /// Render-data only; the renderer uploads the pixels into a texture-array layer.</summary>
    public sealed class SplatLayerImage
    {
        public byte[] AlbedoRgba = Array.Empty<byte>();
        public byte[] NormalRgba = Array.Empty<byte>();
        public Color Tint = Color.White;
        public float TilesPerMetre = 0.25f;
        public float Roughness = 0.85f;
    }

    /// <summary>The per-material fragment uniforms (std140), 112 bytes: per-layer tint+tiling, per-layer scalar
    /// roughness, and globals (triplanar sharpness, projection mode, base specular strength). Field order MUST
    /// mirror the SplatParams UBO block in SplatFrag.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SplatParamsData
    {
        public Vector4 TintTiling0;  // xyz = tint, w = tiles/metre  (layer 0)
        public Vector4 TintTiling1;
        public Vector4 TintTiling2;
        public Vector4 TintTiling3;
        public Vector4 TintTiling4;
        public Vector4 Roughness;    // x..w = roughness for layers 0..3
        public Vector4 Misc;         // x = layer4 roughness, y = triplanarSharpness, z = projectionMode, w = baseSpecStrength
        public const uint SizeInBytes = 112;
    }

    /// <summary>Pure configuration for the 5-layer splat material: the fixed layer count, full mip-chain sizing,
    /// and the std140 params packing. No GPU; headless-testable.</summary>
    public static class SplatMaterialConfig
    {
        /// <summary>Fixed number of splat layers (matches the five terrain weights). The shader hardcodes this.</summary>
        public const int LayerCount = 5;

        /// <summary>Full mip-chain level count for a WxH texture: floor(log2(max(w,h))) + 1.</summary>
        public static uint MipLevelCount(int width, int height)
        {
            int max = Math.Max(1, Math.Max(width, height));
            uint levels = 1;
            while (max > 1) { max >>= 1; levels++; }
            return levels;
        }

        /// <summary>Pack the per-layer scalars (tint/tiling/roughness) + globals into the std140 params block.
        /// Requires exactly <see cref="LayerCount"/> layers, in channel order.</summary>
        public static SplatParamsData BuildParams(IReadOnlyList<SplatLayerImage> layers,
            float triplanarSharpness, SplatProjection projection, float baseSpecStrength)
        {
            if (layers.Count != LayerCount)
                throw new ArgumentException($"a splat material needs exactly {LayerCount} layers, got {layers.Count}.", nameof(layers));

            static Vector4 TintTiling(SplatLayerImage l)
            {
                Vector4 t = l.Tint;
                return new Vector4(t.X, t.Y, t.Z, l.TilesPerMetre);
            }
            return new SplatParamsData
            {
                TintTiling0 = TintTiling(layers[0]),
                TintTiling1 = TintTiling(layers[1]),
                TintTiling2 = TintTiling(layers[2]),
                TintTiling3 = TintTiling(layers[3]),
                TintTiling4 = TintTiling(layers[4]),
                Roughness = new Vector4(layers[0].Roughness, layers[1].Roughness, layers[2].Roughness, layers[3].Roughness),
                Misc = new Vector4(layers[4].Roughness, triplanarSharpness, (float)projection, baseSpecStrength),
            };
        }
    }
}
```

- [ ] **Step 4: Create `SplatMath.cs`.** Create `KhaozEngine.Render3D/Rendering/SplatMath.cs`:

```csharp
using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>CPU mirror of the SplatFrag math (triplanar blend weights, planar mode, and the five-weight
    /// reconstruction from the packed vertex colour). Keep in sync with SplatFrag.</summary>
    public static class SplatMath
    {
        /// <summary>Triplanar blend weights from a surface normal, normalized to sum 1. Higher sharpness biases
        /// toward the dominant axis. Mirrors the shader's pow(abs(N), sharpness) normalize.</summary>
        public static Vector3 TriplanarBlend(Vector3 normal, float sharpness)
        {
            float s = MathF.Max(sharpness, 0.001f);
            var b = new Vector3(
                MathF.Pow(MathF.Abs(normal.X), s),
                MathF.Pow(MathF.Abs(normal.Y), s),
                MathF.Pow(MathF.Abs(normal.Z), s));
            float sum = b.X + b.Y + b.Z;
            return sum > 1e-5f ? b / sum : new Vector3(0f, 1f, 0f);
        }

        /// <summary>Planar (XZ-only) blend weights: project straight down.</summary>
        public static Vector3 PlanarBlend() => new(0f, 1f, 0f);

        /// <summary>Reconstruct the five normalized splat weights from a packed vertex colour (grass/dirt/rock/sand
        /// in rgba, snow = 1 - sum), renormalizing to guard interpolation drift. All-zero -> all grass.</summary>
        public static (float g, float d, float r, float s, float snow) UnpackWeights(Vector4 packed)
        {
            float g = packed.X, d = packed.Y, r = packed.Z, s = packed.W;
            float snow = Math.Clamp(1f - (g + d + r + s), 0f, 1f);
            float sum = g + d + r + s + snow;
            if (sum > 1e-5f) { g /= sum; d /= sum; r /= sum; s /= sum; snow /= sum; }
            else { g = 1f; d = r = s = snow = 0f; }
            return (g, d, r, s, snow);
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter SplatMaterialTests`
Expected: PASS (all 6).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render3D/Rendering/SplatMaterialConfig.cs KhaozEngine.Render3D/Rendering/SplatMath.cs KhaozEngine.Tests/Render3D/SplatMaterialTests.cs
git commit -m "render3d(terrain-pbr-splat): splat material config + UV/weight math (pure)"
```

---

### Task 4: SplatFrag shader + ModelRenderer splat pipeline

**Files:**
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.cs` (add `SplatFrag`)
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.cs` (splat layout, sampler, shaders, pipeline, `CreateSplatMaterialSet`, `BindSplatPass`, `DrawSplatMeshInstanced`, Dispose)

**Interfaces:**
- Consumes: `GpuSamplerFilter.Anisotropic` + `GpuSamplerDescription(...,maximumAnisotropy)` (Task 1); the splat material set is built from two `IGpuTexture` arrays + an `IGpuBuffer` params UBO (Task 5 creates them).
- Produces (on `ModelRenderer`):
  - `IGpuResourceSet CreateSplatMaterialSet(IGpuTexture albedoArray, IGpuTexture normalArray, IGpuBuffer splatParams)`
  - `void BindSplatPass(IGpuCommandList cl)`
  - `void DrawSplatMeshInstanced(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, uint instanceStart, uint instanceCount, IGpuResourceSet splatSet)`

- [ ] **Step 1: Add `SplatFrag` to `ShaderSources.cs`.** In `KhaozEngine.Render3D/Internal/ShaderSources.cs`, after `ModelFrag` (it reuses `ModelVert` as its vertex stage, so no new vertex shader). Add:

```csharp
        // ---- Splat-terrain fragment shader. Pairs with ModelVert (which forwards the packed weights as vColor and
        //      the world position/normal). Reads two 5-layer texture arrays (albedo, tangent-space normal) + a
        //      shared sampler + a per-material params UBO, blends the five layers by the per-vertex weights, tiles
        //      each layer in WORLD space with triplanar projection (no per-vertex tangent needed), and lights with
        //      the SAME key+fill+ambient+point-light+cel model as ModelFrag. Writes the same 3 MRT targets
        //      (geometric normal to attachment 1 for the edge pass). KEEP THE LIGHTING IN SYNC WITH ModelFrag.
        //      Sample the two arrays in binding order (Albedo then Normal) - the Metal SPIRV-Cross first-sample-order
        //      constraint, same as ModelFrag/EdgeFrag. ----
        public const string SplatFrag = @"#version 450
layout(set=0, binding=0) uniform U {
    mat4 ViewProj;
    vec4 LightDir; vec4 LightColor; vec4 Ambient; vec4 Params;
    vec4 FillDir; vec4 FillColor; vec4 CameraPos;
    vec4 PointPosRadius[16];
    vec4 PointColorIntensity[16];
};
layout(set=0, binding=1) uniform texture2DArray AlbedoArray;
layout(set=0, binding=2) uniform texture2DArray NormalArray;
layout(set=0, binding=3) uniform sampler Samp;
layout(set=0, binding=4) uniform SplatParams {
    vec4 TintTiling[5];   // xyz = tint, w = tiles/metre
    vec4 Roughness;       // x..w = roughness for layers 0..3
    vec4 Misc;            // x = layer4 roughness, y = triplanarSharpness, z = projectionMode, w = baseSpecStrength
};
layout(location=0) in vec3 vNormalW;
layout(location=1) in vec4 vColor;       // packed weights (grass,dirt,rock,sand); snow = 1 - sum
layout(location=2) in float vDepth;
layout(location=3) in vec3 vWorldPos;
layout(location=4) in vec2 vUv;          // grid uv (unused; world-space UV is used instead)
layout(location=5) in vec4 vTint;
layout(location=6) in vec4 vEmissive;
layout(location=7) in vec4 vSpecParams;  // unused for terrain (base spec comes from Misc.w)
layout(location=8) in vec4 vTangent;     // unused (triplanar derives its own basis)
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;

vec3 sampleAlbedo(int layer, vec2 uvx, vec2 uvy, vec2 uvz, vec3 bw) {
    vec3 ax = texture(sampler2DArray(AlbedoArray, Samp), vec3(uvx, float(layer))).rgb;
    vec3 ay = texture(sampler2DArray(AlbedoArray, Samp), vec3(uvy, float(layer))).rgb;
    vec3 az = texture(sampler2DArray(AlbedoArray, Samp), vec3(uvz, float(layer))).rgb;
    return ax*bw.x + ay*bw.y + az*bw.z;
}

// Whiteout triplanar normal blend (reorient each plane's tangent-space normal into world space, no vertex tangent).
vec3 sampleNormal(int layer, vec2 uvx, vec2 uvy, vec2 uvz, vec3 bw, vec3 Ngeo) {
    vec3 nx = texture(sampler2DArray(NormalArray, Samp), vec3(uvx, float(layer))).xyz * 2.0 - 1.0;
    vec3 ny = texture(sampler2DArray(NormalArray, Samp), vec3(uvy, float(layer))).xyz * 2.0 - 1.0;
    vec3 nz = texture(sampler2DArray(NormalArray, Samp), vec3(uvz, float(layer))).xyz * 2.0 - 1.0;
    nx = vec3(nx.xy + Ngeo.zy, abs(nx.z) * Ngeo.x);
    ny = vec3(ny.xy + Ngeo.xz, abs(ny.z) * Ngeo.y);
    nz = vec3(nz.xy + Ngeo.xy, abs(nz.z) * Ngeo.z);
    return normalize(nx.zyx * bw.x + ny.xzy * bw.y + nz.xyz * bw.z);
}

void main() {
    vec3 Ngeo = normalize(vNormalW);

    // Reconstruct + renormalize the five weights (4 packed in vColor, snow = 1 - sum).
    float a0 = vColor.r, a1 = vColor.g, a2 = vColor.b, a3 = vColor.a;
    float a4 = clamp(1.0 - (a0 + a1 + a2 + a3), 0.0, 1.0);
    float wsum = a0 + a1 + a2 + a3 + a4;
    if (wsum > 1e-5) { a0/=wsum; a1/=wsum; a2/=wsum; a3/=wsum; a4/=wsum; } else { a0 = 1.0; a1 = a2 = a3 = a4 = 0.0; }
    float w[5] = float[5](a0, a1, a2, a3, a4);
    float rgh[5] = float[5](Roughness.x, Roughness.y, Roughness.z, Roughness.w, Misc.x);

    // Triplanar blend weights (planar mode forces the XZ plane).
    int projMode = int(Misc.z + 0.5);
    vec3 bw;
    if (projMode == 1) { bw = vec3(0.0, 1.0, 0.0); }
    else {
        bw = pow(abs(Ngeo), vec3(max(Misc.y, 0.001)));
        bw /= max(bw.x + bw.y + bw.z, 1e-5);
    }

    vec3 albedo = vec3(0.0);
    vec3 Nsum = vec3(0.0);
    float rough = 0.0;
    for (int L = 0; L < 5; L++) {
        float wl = w[L];
        if (wl <= 0.001) continue;
        float tile = TintTiling[L].w;
        vec2 uvx = vWorldPos.yz * tile;
        vec2 uvy = vWorldPos.xz * tile;
        vec2 uvz = vWorldPos.xy * tile;
        albedo += wl * sampleAlbedo(L, uvx, uvy, uvz, bw) * TintTiling[L].xyz;
        Nsum   += wl * sampleNormal(L, uvx, uvy, uvz, bw, Ngeo);
        rough  += wl * rgh[L];
    }
    albedo *= vTint.rgb;
    vec3 N = (dot(Nsum, Nsum) > 1e-8) ? normalize(Nsum) : Ngeo;

    // Lighting: mirror ModelFrag. Base specular from Misc.w, modulated by the blended roughness.
    float specStrength = Misc.w * (1.0 - rough);
    float specExp = max(mix(48.0, 8.0, rough), 1.0);
    float ndlKey  = max(dot(N, -normalize(LightDir.xyz)), 0.0);
    float ndlFill = max(dot(N, -normalize(FillDir.xyz)), 0.0);
    float bands = Params.x;
    if (bands >= 1.0) { ndlKey = floor(ndlKey*bands+0.5)/bands; ndlFill = floor(ndlFill*bands+0.5)/bands; }
    vec3 diffuse = LightColor.rgb*ndlKey + FillColor.rgb*ndlFill;
    vec3 V = normalize(CameraPos.xyz - vWorldPos);
    vec3 H = normalize(-normalize(LightDir.xyz) + V);
    float spec = pow(max(dot(N,H),0.0), specExp) * specStrength * step(0.0001, ndlKey);
    vec3 specColor = LightColor.rgb*spec;
    int npl = int(Params.y);
    for (int i = 0; i < npl; i++) {
        vec3 toL = PointPosRadius[i].xyz - vWorldPos;
        float radius = PointPosRadius[i].w;
        float dist = length(toL);
        vec3 L = (dist > 1e-4) ? toL / dist : vec3(0.0);
        float ndl = max(dot(N, L), 0.0);
        if (bands >= 1.0) ndl = floor(ndl*bands+0.5)/bands;
        float f = clamp(1.0 - (dist*dist)/max(radius*radius, 1e-6), 0.0, 1.0);
        float att = f * f * PointColorIntensity[i].w;
        vec3 lc = PointColorIntensity[i].rgb;
        diffuse += lc * (ndl * att);
        vec3 Hp = normalize(L + V);
        float sp = pow(max(dot(N,Hp),0.0), specExp) * specStrength * step(0.0001, ndl);
        specColor += lc * (sp * att);
    }
    vec3 lit = albedo * (Ambient.rgb + diffuse) + specColor + vEmissive.rgb;
    oColor = vec4(lit, 1.0);
    oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0); // GEOMETRIC normal for the edge pass
    oDepth = vec4(vDepth, vDepth, vDepth, 1.0);
}";
```

- [ ] **Step 2: Add the splat fields to `ModelRenderer`.** In `KhaozEngine.Render3D/Rendering/ModelRenderer.cs`, after the `_pipeline`/`_shaders` field declarations (line 67-68), add:

```csharp
        // Splat-terrain pipeline (5-layer texture-array PBR, weights in vertex Color, triplanar). Shares _ubo
        // (frame uniforms) and the instance buffer; its own layout/sampler/shaders/pipeline.
        readonly IGpuResourceLayout _splatLayout;
        readonly IGpuSampler _terrainSampler;   // wrap + anisotropic (trilinear fallback); OWNED here (dispose it)
        readonly IGpuShaderSet _splatShaders;
        readonly IGpuPipeline _splatPipeline;
```

- [ ] **Step 3: Build the splat pipeline in the ctor.** In the `ModelRenderer` constructor, at the END (after the `_pipeline = factory.CreateGraphicsPipeline(...)` block closes at line 163, before the closing `}` of the ctor), add. Note `vertexLayout` and `instanceLayout` are still in scope from the model-pipeline setup and are reused:

```csharp
            _splatLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("AlbedoArray", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("NormalArray", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Sampler", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("SplatParams", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));

            // Tileable detail textures REPEAT across the world, so wrap addressing; anisotropic for grazing ground
            // (CreateSampler falls back to trilinear when the backend lacks anisotropy).
            _terrainSampler = factory.CreateSampler(new GpuSamplerDescription(
                GpuSamplerFilter.Anisotropic, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap, maximumAnisotropy: 8));

            _splatShaders = factory.CreateShadersFromSpirv(ShaderSources.ModelVert, ShaderSources.SplatFrag);

            _splatPipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _splatLayout },
                ShaderSet = _splatShaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout },
                Outputs = modelOutputs,
            });
```

- [ ] **Step 4: Add the splat methods.** In `ModelRenderer`, after `CreateMaterialSet` (line 249), add:

```csharp
        /// <summary>Build a splat-terrain material resource set: the shared frame UBO + the two 5-layer texture
        /// arrays (albedo, tangent-space normal) + the terrain (wrap/anisotropic) sampler + the per-material params
        /// UBO. Shared across every chunk using this material; owned by Scene3D, NOT per mesh.</summary>
        public IGpuResourceSet CreateSplatMaterialSet(IGpuTexture albedoArray, IGpuTexture normalArray, IGpuBuffer splatParams) =>
            _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                _splatLayout, _ubo, albedoArray, normalArray, _terrainSampler, splatParams));

        /// <summary>Bind the splat-terrain pipeline for the splat pass (call once before the splat draw loop). The
        /// frame UBO (shared with the model pass) is already populated by <see cref="SetFrameUniforms"/>.</summary>
        public void BindSplatPass(IGpuCommandList cl) => cl.SetPipeline(_splatPipeline);

        /// <summary>Draw one splat-terrain mesh run through the splat pipeline, reusing the shared instance buffer
        /// (terrain instances are identity-transform, white-tint). <see cref="BindSplatPass"/> must be bound.</summary>
        public void DrawSplatMeshInstanced(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
            GpuIndexFormat indexFormat, uint instanceStart, uint instanceCount, IGpuResourceSet splatSet)
        {
            cl.SetGraphicsResourceSet(0, splatSet);
            cl.SetVertexBuffer(0, vb);
            cl.SetVertexBuffer(1, _instanceBuffer!);
            cl.SetIndexBuffer(ib, indexFormat);
            cl.DrawIndexed((uint)indexCount, instanceCount, 0, 0, instanceStart);
        }
```

- [ ] **Step 5: Dispose the splat resources.** In `ModelRenderer.Dispose` (line 325-336), after the existing `_pipeline.Dispose(); ...` line add:

```csharp
            _splatPipeline.Dispose(); _splatLayout.Dispose(); _splatShaders.Dispose(); _terrainSampler.Dispose();
```

- [ ] **Step 6: Build to verify the shader cross-compiles + the renderer compiles.**

Run: `dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj -c Debug`
Expected: build succeeds. (The shader is cross-compiled at device/pipeline creation time, exercised by the Task 5 GpuFact; this step only confirms the C# compiles and the GLSL string is wired.)

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Render3D/Internal/ShaderSources.cs KhaozEngine.Render3D/Rendering/ModelRenderer.cs
git commit -m "render3d(terrain-pbr-splat): SplatFrag shader + ModelRenderer splat pipeline"
```

---

### Task 5: Scene3D splat material storage + load/draw/unload wiring

**Files:**
- Modify: `KhaozEngine.Render3D/Scene3D.cs` (SplatMaterialHandle, `_splatMaterials`, `LoadSplatMaterial`, `LoadMesh(mesh, handle)`, `LoadMeshInternal` signature, `Mesh.SplatMaterial`, the splat draw pass, `UnloadSplatMaterial`, Dispose)
- Test: `KhaozEngine.Tests/Render3D/SplatRenderGpuTests.cs` (create)

**Interfaces:**
- Consumes: `ModelRenderer.CreateSplatMaterialSet/BindSplatPass/DrawSplatMeshInstanced` (Task 4); `SplatMaterialConfig`/`SplatParamsData`/`SplatLayerImage`/`SplatProjection` (Task 3); `GpuTextureDescription.Texture2DArray`, `IGpuDevice.UpdateTexture(...,mipLevel,arrayLayer)`, `IGpuCommandList.GenerateMipmaps` (Task 2).
- Produces (on `Scene3D`):
  - `readonly struct SplatMaterialHandle { bool IsValid; static SplatMaterialHandle Invalid; }`
  - `SplatMaterialHandle LoadSplatMaterial(int width, int height, IReadOnlyList<SplatLayerImage> layers, float triplanarSharpness = 8f, SplatProjection projection = SplatProjection.Triplanar, float baseSpecStrength = 0.15f)`
  - `MeshHandle LoadMesh(GltfMesh mesh, SplatMaterialHandle material)`
  - `void UnloadSplatMaterial(SplatMaterialHandle h)`

- [ ] **Step 1: Write the failing GpuFact** — load a 5-layer splat material from raw RGBA, load a 2-triangle mesh with packed weights, draw it, render to an offscreen target without throwing.

Create `KhaozEngine.Tests/Render3D/SplatRenderGpuTests.cs`:

```csharp
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class SplatRenderGpuTests
    {
        static List<SplatLayerImage> FiveSolidLayers(int size)
        {
            var layers = new List<SplatLayerImage>();
            byte[][] colors =
            {
                new byte[] { 60, 110, 40, 255 },   // grass
                new byte[] { 90, 75, 55, 255 },    // dirt
                new byte[] { 110, 105, 100, 255 }, // rock
                new byte[] { 190, 175, 125, 255 }, // sand
                new byte[] { 235, 238, 245, 255 }, // snow
            };
            foreach (var c in colors)
            {
                var albedo = new byte[size * size * 4];
                var normal = new byte[size * size * 4];
                for (int p = 0; p < albedo.Length; p += 4)
                {
                    albedo[p] = c[0]; albedo[p + 1] = c[1]; albedo[p + 2] = c[2]; albedo[p + 3] = 255;
                    normal[p] = 128; normal[p + 1] = 128; normal[p + 2] = 255; normal[p + 3] = 255; // flat
                }
                layers.Add(new SplatLayerImage { AlbedoRgba = albedo, NormalRgba = normal, TilesPerMetre = 0.25f, Roughness = 0.8f });
            }
            return layers;
        }

        [GpuFact]
        public void SplatTerrainMeshRendersWithoutThrowing()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;
            const int W = 64, H = 48;
            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);
            using var scene = new Scene3D(gd, finalFB.Outputs);
            using IGpuCommandList cl = f.CreateCommandList();

            var mat = scene.LoadSplatMaterial(8, 8, FiveSolidLayers(8));

            // A flat quad on the ground; Color carries packed weights (all grass here: (1,0,0,0)).
            var w = new Vector4(1f, 0f, 0f, 0f);
            var verts = new[]
            {
                new ModelVertex(new Vector3(-1, 0, -1), Vector3.UnitY, w, new Vector2(0, 0)),
                new ModelVertex(new Vector3( 1, 0, -1), Vector3.UnitY, w, new Vector2(1, 0)),
                new ModelVertex(new Vector3( 1, 0,  1), Vector3.UnitY, w, new Vector2(1, 1)),
                new ModelVertex(new Vector3(-1, 0,  1), Vector3.UnitY, w, new Vector2(0, 1)),
            };
            var mesh = new GltfMesh(verts, new ushort[] { 0, 1, 2, 0, 2, 3 });
            var handle = scene.LoadMesh(mesh, mat);

            scene.Begin();
            scene.Draw(handle, Matrix4x4.Identity, Color.White);
            cl.Begin();
            scene.RenderInternal(cl, W, H, finalFB);
            cl.End();
            gd.Submit(cl);
            gd.WaitForIdle();

            scene.UnloadMesh(handle);
            scene.UnloadSplatMaterial(mat);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter SplatTerrainMeshRendersWithoutThrowing`
Expected: FAIL to compile (`LoadSplatMaterial`, `LoadMesh(mesh, SplatMaterialHandle)`, `UnloadSplatMaterial` undefined).

- [ ] **Step 3: Add `SplatMaterialHandle` + storage.** In `KhaozEngine.Render3D/Scene3D.cs`, after the `SurfaceMaps` struct (line 166), add the handle:

```csharp
        /// <summary>An opaque handle to a splat-terrain material (5 tileable layers + triplanar params) loaded with
        /// <see cref="LoadSplatMaterial"/>. Pass it to <see cref="LoadMesh(GltfMesh,SplatMaterialHandle)"/> to draw a
        /// mesh through the splat pipeline. Shared across many meshes (e.g. every terrain chunk).</summary>
        public readonly struct SplatMaterialHandle
        {
            internal readonly int Index;
            internal SplatMaterialHandle(int index) { Index = index + 1; } // store +1 so default == Invalid
            public static SplatMaterialHandle Invalid => default;
            public bool IsValid => Index != 0;
            internal int ListIndex => Index - 1;
        }
```

Next to the `_textures` field (line 41), add the splat-material list and the owning type. Put the nested `SplatMaterialEntry` class beside the `Mesh` struct (near line 944):

```csharp
        // Loaded splat-terrain materials, indexed by SplatMaterialHandle.ListIndex. Each owns its two texture
        // arrays + params UBO + resource set; shared across meshes; disposed in Dispose / UnloadSplatMaterial.
        readonly List<SplatMaterialEntry?> _splatMaterials = new();
```

```csharp
        /// <summary>A loaded splat-terrain material: the two 5-layer texture arrays (albedo, normal), the per-material
        /// params UBO, and the resource set. Owned by Scene3D; shared by every mesh that uses it.</summary>
        sealed class SplatMaterialEntry
        {
            public readonly IGpuTexture AlbedoArray, NormalArray;
            public readonly IGpuBuffer ParamsUbo;
            public readonly IGpuResourceSet Set;
            public SplatMaterialEntry(IGpuTexture albedo, IGpuTexture normal, IGpuBuffer paramsUbo, IGpuResourceSet set)
            { AlbedoArray = albedo; NormalArray = normal; ParamsUbo = paramsUbo; Set = set; }
            public void Dispose() { Set.Dispose(); AlbedoArray.Dispose(); NormalArray.Dispose(); ParamsUbo.Dispose(); }
        }
```

- [ ] **Step 4: Add `LoadSplatMaterial`.** In `KhaozEngine.Render3D/Scene3D.cs`, after `LoadTexture(byte[], int, int)` (line 251), add:

```csharp
        /// <summary>Upload a 5-layer splat-terrain material: two texture arrays (albedo + tangent-space normal, one
        /// layer per <see cref="SplatLayerImage"/>, all the same <paramref name="width"/> x <paramref name="height"/>
        /// RGBA8), with full mip chains generated, plus a params UBO (per-layer tint/tiling/roughness + triplanar
        /// sharpness + projection + base specular). Returns a handle to draw meshes through the splat pipeline. The
        /// material is owned by the scene and freed in <see cref="Dispose"/> (or <see cref="UnloadSplatMaterial"/>);
        /// it is shared across every mesh that references it (e.g. all terrain chunks).</summary>
        public SplatMaterialHandle LoadSplatMaterial(int width, int height, IReadOnlyList<SplatLayerImage> layers,
            float triplanarSharpness = 8f, SplatProjection projection = SplatProjection.Triplanar, float baseSpecStrength = 0.15f)
        {
            if (layers.Count != SplatMaterialConfig.LayerCount)
                throw new ArgumentException($"a splat material needs exactly {SplatMaterialConfig.LayerCount} layers, got {layers.Count}.", nameof(layers));
            var f = _gd.Factory;
            uint w = (uint)width, h = (uint)height, mips = SplatMaterialConfig.MipLevelCount(width, height);
            const GpuTextureUsage usage = GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps;
            var albedo = f.CreateTexture(GpuTextureDescription.Texture2DArray(w, h, GpuPixelFormat.R8G8B8A8UNorm, usage, (uint)layers.Count, mips));
            var normal = f.CreateTexture(GpuTextureDescription.Texture2DArray(w, h, GpuPixelFormat.R8G8B8A8UNorm, usage, (uint)layers.Count, mips));
            for (int L = 0; L < layers.Count; L++)
            {
                _gd.UpdateTexture(albedo, layers[L].AlbedoRgba, 0, 0, w, h, mipLevel: 0, arrayLayer: (uint)L);
                _gd.UpdateTexture(normal, layers[L].NormalRgba, 0, 0, w, h, mipLevel: 0, arrayLayer: (uint)L);
            }
            // Generate both mip chains in one transient command list.
            var cl = f.CreateCommandList();
            cl.Begin();
            cl.GenerateMipmaps(albedo);
            cl.GenerateMipmaps(normal);
            cl.End();
            _gd.Submit(cl);
            _gd.WaitForIdle();
            cl.Dispose();

            var data = SplatMaterialConfig.BuildParams(layers, triplanarSharpness, projection, baseSpecStrength);
            var ubo = f.CreateBuffer(new GpuBufferDescription(SplatParamsData.SizeInBytes, GpuBufferUsage.UniformBuffer));
            _gd.UpdateBuffer(ubo, 0, in data);

            var set = _model.CreateSplatMaterialSet(albedo, normal, ubo);
            _splatMaterials.Add(new SplatMaterialEntry(albedo, normal, ubo, set));
            return new SplatMaterialHandle(_splatMaterials.Count - 1);
        }
```

- [ ] **Step 5: Add `LoadMesh(mesh, handle)` + thread a splat index through `LoadMeshInternal` + `Mesh`.** In `KhaozEngine.Render3D/Scene3D.cs`:

After `LoadMesh(GltfMesh mesh, SurfaceMaps maps)` (line 197), add:

```csharp
        /// <summary>Upload a mesh and draw it through the splat-terrain pipeline with <paramref name="material"/>
        /// (its vertex <c>Color</c> carries the packed splat weights). An invalid handle falls back to the untextured
        /// model path. The splat material is shared (owned by the scene); unloading the mesh does NOT free it.</summary>
        public MeshHandle LoadMesh(GltfMesh mesh, SplatMaterialHandle material)
        {
            if (!material.IsValid) return LoadMesh(mesh);
            return LoadMeshInternal(mesh, null, material.ListIndex);
        }
```

Change `LoadMeshInternal` (line 199) signature + the `Mesh` construction:

```csharp
        MeshHandle LoadMeshInternal(GltfMesh mesh, IGpuResourceSet? material, int splatMaterial = -1)
        {
            var f = _gd.Factory;
            var vb = f.CreateBuffer(new GpuBufferDescription((uint)(mesh.Vertices.Length * ModelVertex.SizeInBytes), GpuBufferUsage.VertexBuffer));
            _gd.UpdateBuffer(vb, 0, mesh.Vertices);
            var ib = CreateIndexBuffer(mesh.Indices32, mesh.IndexFormat);

            int index = _slots.Alloc(out int generation);
            var slot = new Mesh(vb, ib, mesh.Indices32.Length, mesh.IndexFormat, material, splatMaterial);
            if (index < _meshes.Count) _meshes[index] = slot;
            else _meshes.Add(slot);
            return new MeshHandle(index, generation);
        }
```

Change the `Mesh` struct (line 930-944) to carry the splat index:

```csharp
        readonly struct Mesh
        {
            public readonly IGpuBuffer Vb, Ib;
            public readonly int IndexCount;
            public readonly GpuIndexFormat IndexFormat;
            public readonly IGpuResourceSet? MaterialSet;
            /// <summary>Index into Scene3D's splat-material list when this mesh draws through the splat pipeline, else
            /// -1 (the normal model pipeline). Splat meshes carry no per-mesh <see cref="MaterialSet"/> (the splat set
            /// is shared and owned by the scene), so unload frees only Vb/Ib.</summary>
            public readonly int SplatMaterial;
            public Mesh(IGpuBuffer vb, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, IGpuResourceSet? materialSet = null, int splatMaterial = -1)
            {
                Vb = vb; Ib = ib; IndexCount = indexCount; IndexFormat = indexFormat; MaterialSet = materialSet; SplatMaterial = splatMaterial;
            }
        }
```

- [ ] **Step 6: Route splat meshes through the splat pass in `RenderInternal`.** In `KhaozEngine.Render3D/Scene3D.cs`, in the rigid-instancing block (lines 740-752), replace the `foreach (var run in _runs)` loop and add the splat pass after it (still inside `if (_instanceData.Count > 0)`):

```csharp
                _model.UploadInstances(cl, CollectionsMarshal.AsSpan(_instanceData));
                foreach (var run in _runs)
                {
                    if (!_slots.IsValid(run.Mesh.Index, run.Mesh.Generation)) continue;
                    var m = _meshes[run.Mesh.Index];
                    if (m is not { } mesh) continue;
                    if (mesh.SplatMaterial >= 0) continue;   // drawn in the splat pass below
                    _model.DrawMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, run.Start, run.Count, mesh.MaterialSet);
                }
                // Splat-terrain pass: same uploaded instance buffer, the dedicated 5-layer texture-array pipeline.
                bool splatBound = false;
                foreach (var run in _runs)
                {
                    if (!_slots.IsValid(run.Mesh.Index, run.Mesh.Generation)) continue;
                    var m = _meshes[run.Mesh.Index];
                    if (m is not { } mesh) continue;
                    if (mesh.SplatMaterial < 0) continue;
                    var sm = _splatMaterials[mesh.SplatMaterial];
                    if (sm is null) continue;
                    if (!splatBound) { _model.BindSplatPass(cl); splatBound = true; }
                    _model.DrawSplatMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, run.Start, run.Count, sm.Set);
                }
```

(The skinned pass that follows already re-binds the model pipeline via `_model.BindPass(cl)`, so binding the splat pipeline here is safe.)

- [ ] **Step 7: Add `UnloadSplatMaterial` + dispose the list.** After `UnloadMesh` (line 294), add:

```csharp
        /// <summary>Free a splat-terrain material's GPU resources (its texture arrays, params UBO, resource set) and
        /// release its slot. A <c>default</c>/Invalid handle is a no-op. Meshes still referencing it must be unloaded
        /// first (they hold no reference after this).</summary>
        public void UnloadSplatMaterial(SplatMaterialHandle h)
        {
            if (!h.IsValid) return;
            var m = _splatMaterials[h.ListIndex];
            m?.Dispose();
            _splatMaterials[h.ListIndex] = null;
        }
```

In `Dispose` (line 909-928), after `_textures.Clear();` add:

```csharp
            foreach (var s in _splatMaterials) s?.Dispose();
            _splatMaterials.Clear();
```

- [ ] **Step 8: Run the GpuFact + the full suite to verify nothing regressed**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter SplatTerrainMeshRendersWithoutThrowing`
Then: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: the GpuFact passes on a device; the existing golden/model suite stays green (the model pass is unchanged for non-splat meshes).

- [ ] **Step 9: Commit**

```bash
git add KhaozEngine.Render3D/Scene3D.cs KhaozEngine.Tests/Render3D/SplatRenderGpuTests.cs
git commit -m "render3d(terrain-pbr-splat): Scene3D splat material load/draw/unload wiring"
```

---

### Task 6: Terrain.Render3D mapping (packing, config, scene extensions, presets, sink)

**Files:**
- Create: `KhaozEngine.Terrain.Render3D/TerrainSplatPacking.cs`
- Create: `KhaozEngine.Terrain.Render3D/TerrainLayeredMaterial.cs` (`TerrainMaterialLayer` + `TerrainLayeredMaterial`)
- Create: `KhaozEngine.Terrain.Render3D/TerrainMaterialPresets.cs`
- Modify: `KhaozEngine.Terrain.Render3D/TerrainScene3D.cs` (add `LoadTerrainMaterial` + the textured `LoadTerrainChunk` overload)
- Modify: `KhaozEngine.Terrain.Render3D/Scene3DChunkSink.cs` (optional material)
- Test: `KhaozEngine.Tests/Terrain/TerrainSplatPackingTests.cs` (create); add a `GpuFact` for the textured sink in `KhaozEngine.Tests/Terrain/` if a Scene3D GpuFact harness is reachable from the Terrain tests namespace.

**Interfaces:**
- Consumes: `Scene3D.LoadSplatMaterial`, `Scene3D.LoadMesh(mesh, SplatMaterialHandle)`, `SplatMaterialHandle`, `SplatLayerImage`, `SplatProjection` (Task 5); `TerrainChunkMesh.Mesh`/`.Splat`, `TerrainChunkBuilder.Build`, `TerrainField`, `PropScatter`, `ChunkGrid` (existing).
- Produces:
  - `static class TerrainSplatPacking { static Vector4 Pack(in TerrainSplatWeights w); static GltfMesh PackedMesh(TerrainChunkMesh chunk); }`
  - `sealed class TerrainMaterialLayer { byte[] AlbedoRgba; byte[] NormalRgba; Color Tint; float TilesPerMetre; float Roughness; }`
  - `sealed class TerrainLayeredMaterial { int Width, Height; TerrainMaterialLayer Grass/Dirt/Rock/Sand/Snow; float TriplanarSharpness; SplatProjection Projection; float BaseSpecStrength; IReadOnlyList<TerrainMaterialLayer> Layers; void Validate(); }`
  - `static class TerrainMaterialPresets { static TerrainLayeredMaterial Procedural(int size = 128); }`
  - `TerrainScene3D.LoadTerrainMaterial(this Scene3D, TerrainLayeredMaterial) -> SplatMaterialHandle`
  - `TerrainScene3D.LoadTerrainChunk(this Scene3D, TerrainChunkMesh, SplatMaterialHandle) -> MeshHandle`
  - `Scene3DChunkSink(..., SplatMaterialHandle material = default)`

- [ ] **Step 1: Write the failing packing test.** Create `KhaozEngine.Tests/Terrain/TerrainSplatPackingTests.cs`:

```csharp
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class TerrainSplatPackingTests
    {
        [Fact]
        public void PackThenUnpackRoundTripsTheFiveWeights()
        {
            var w = TerrainSplatWeights.From(height: 30f, slope01: 0.3f, biome: default, waterLevel: 0f, snowLine: 60f);
            Vector4 packed = TerrainSplatPacking.Pack(w);
            var (g, d, r, s, snow) = SplatMath.UnpackWeights(packed);
            Assert.Equal(w.Grass, g, 4);
            Assert.Equal(w.Dirt, d, 4);
            Assert.Equal(w.Rock, r, 4);
            Assert.Equal(w.Sand, s, 4);
            Assert.Equal(w.Snow, snow, 4);
        }

        [Fact]
        public void PackedMeshCarriesWeightsInColorForEveryVertex()
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            var region = new TerrainChunkRegion { OriginX = 0f, OriginZ = 0f, Size = 32f };
            var chunk = TerrainChunkBuilder.Build(field, region, lod: 0);
            var packed = TerrainSplatPacking.PackedMesh(chunk);

            Assert.Equal(chunk.Mesh.Vertices.Length, packed.Vertices.Length);
            for (int i = 0; i < packed.Vertices.Length; i++)
                Assert.Equal(TerrainSplatPacking.Pack(chunk.Splat[i]), packed.Vertices[i].Color);
        }

        [Fact]
        public void ProceduralMaterialValidates()
        {
            var mat = TerrainMaterialPresets.Procedural(size: 16);
            mat.Validate();   // throws on a malformed material; reaching here is the assertion
            Assert.Equal(5, mat.Layers.Count);
            Assert.Equal(16 * 16 * 4, mat.Grass.AlbedoRgba.Length);
        }
    }
}
```

(`TerrainChunkRegion` uses an object-initializer and the field is built via `new TerrainField(TerrainPresets.Clearing())`, matching `KhaozEngine.Tests/Terrain/TerrainChunkBuilderTests.cs`. `TerrainSplatWeights.From` takes `(height, slope01, BiomeId biome, waterLevel, snowLine)`.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter TerrainSplatPackingTests`
Expected: FAIL to compile.

- [ ] **Step 3: Create `TerrainSplatPacking.cs`.** Create `KhaozEngine.Terrain.Render3D/TerrainSplatPacking.cs`:

```csharp
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>Packs the baked per-vertex <see cref="TerrainSplatWeights"/> into a mesh the splat pipeline reads:
    /// the four leading weights (grass/dirt/rock/sand) ride in <see cref="ModelVertex.Color"/> (full float), and the
    /// shader reconstructs snow as 1 - sum. Pure; headless-testable. The untextured path is unchanged (it keeps the
    /// ramp Color the builder bakes).</summary>
    public static class TerrainSplatPacking
    {
        /// <summary>Pack the four leading weights into an RGBA colour (snow is derived in the shader).</summary>
        public static Vector4 Pack(in TerrainSplatWeights w) => new(w.Grass, w.Dirt, w.Rock, w.Sand);

        /// <summary>Build a render mesh whose vertex <c>Color</c> carries the packed splat weights (Position/Normal/
        /// Uv/Tangent copied from the chunk's mesh; indices shared). Hand the result to
        /// <c>Scene3D.LoadMesh(mesh, SplatMaterialHandle)</c>.</summary>
        public static GltfMesh PackedMesh(TerrainChunkMesh chunk)
        {
            var src = chunk.Mesh.Vertices;
            var splat = chunk.Splat;
            var verts = new ModelVertex[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var v = src[i];
                verts[i] = new ModelVertex(v.Position, v.Normal, Pack(splat[i]), v.Uv, v.Tangent);
            }
            return new GltfMesh(verts, chunk.Mesh.Indices32);
        }
    }
}
```

- [ ] **Step 4: Create `TerrainLayeredMaterial.cs`.** Create `KhaozEngine.Terrain.Render3D/TerrainLayeredMaterial.cs`:

```csharp
using System;
using System.Collections.Generic;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>One terrain surface layer: a tileable albedo + tangent-space normal (RGBA8, same WxH as every other
    /// layer), a tint, the tiling rate (tiles per world metre), and a scalar roughness.</summary>
    public sealed class TerrainMaterialLayer
    {
        public byte[] AlbedoRgba = Array.Empty<byte>();
        public byte[] NormalRgba = Array.Empty<byte>();
        public Color Tint = Color.White;
        public float TilesPerMetre = 0.25f;
        public float Roughness = 0.85f;
    }

    /// <summary>The five terrain material layers in channel order (grass/dirt/rock/sand/snow, matching
    /// <see cref="TerrainSplatWeights"/>) plus global render params. Realize it once with
    /// <c>scene.LoadTerrainMaterial(...)</c>; the resulting handle is shared by every chunk.</summary>
    public sealed class TerrainLayeredMaterial
    {
        public int Width;
        public int Height;
        public TerrainMaterialLayer Grass = new();
        public TerrainMaterialLayer Dirt = new();
        public TerrainMaterialLayer Rock = new();
        public TerrainMaterialLayer Sand = new();
        public TerrainMaterialLayer Snow = new();
        public float TriplanarSharpness = 8f;
        public SplatProjection Projection = SplatProjection.Triplanar;
        public float BaseSpecStrength = 0.15f;

        /// <summary>The five layers in channel order (grass, dirt, rock, sand, snow).</summary>
        public IReadOnlyList<TerrainMaterialLayer> Layers => new[] { Grass, Dirt, Rock, Sand, Snow };

        /// <summary>Throw if the material is malformed: non-positive dimensions, or any layer whose albedo/normal
        /// byte length does not match Width*Height*4 (the texture-array layers must all be the same RGBA8 size).</summary>
        public void Validate()
        {
            if (Width <= 0 || Height <= 0)
                throw new ArgumentException($"TerrainLayeredMaterial needs positive dimensions, got {Width}x{Height}.");
            int expected = Width * Height * 4;
            var layers = Layers;
            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i].AlbedoRgba.Length != expected)
                    throw new ArgumentException($"layer {i} albedo is {layers[i].AlbedoRgba.Length} bytes, expected {expected} ({Width}x{Height} RGBA8).");
                if (layers[i].NormalRgba.Length != expected)
                    throw new ArgumentException($"layer {i} normal is {layers[i].NormalRgba.Length} bytes, expected {expected} ({Width}x{Height} RGBA8).");
            }
        }
    }
}
```

- [ ] **Step 5: Create `TerrainMaterialPresets.cs`.** Create `KhaozEngine.Terrain.Render3D/TerrainMaterialPresets.cs`:

```csharp
using KhaozEngine.Primitives;

namespace KhaozEngine.Terrain
{
    /// <summary>Procedural placeholder terrain materials so the in-repo sample and tests run without shipping binary
    /// textures. Real games supply ambientCG-style CC0 tileable albedo/normal per layer. Deterministic (a coordinate
    /// hash, no RNG); proves the full splat pipeline (arrays, mips, triplanar, normal maps) end to end.</summary>
    public static class TerrainMaterialPresets
    {
        /// <summary>A five-layer material with tinted value-noise albedo + a gentle derived normal per layer
        /// (grass/dirt/rock/sand/snow), all <paramref name="size"/> x <paramref name="size"/> RGBA8.</summary>
        public static TerrainLayeredMaterial Procedural(int size = 128)
        {
            var grass = Layer(size, new Color(0.27f, 0.42f, 0.18f), roughness: 0.9f, tiles: 0.35f);
            var dirt  = Layer(size, new Color(0.34f, 0.30f, 0.24f), roughness: 0.9f, tiles: 0.30f);
            var rock  = Layer(size, new Color(0.44f, 0.42f, 0.40f), roughness: 0.7f, tiles: 0.20f);
            var sand  = Layer(size, new Color(0.76f, 0.70f, 0.50f), roughness: 0.85f, tiles: 0.40f);
            var snow  = Layer(size, new Color(0.93f, 0.94f, 0.96f), roughness: 0.4f, tiles: 0.25f);
            return new TerrainLayeredMaterial
            {
                Width = size, Height = size,
                Grass = grass, Dirt = dirt, Rock = rock, Sand = sand, Snow = snow,
            };
        }

        static TerrainMaterialLayer Layer(int size, Color baseColor, float roughness, float tiles)
        {
            var albedo = new byte[size * size * 4];
            var normal = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                float n = Noise(x, y);                 // 0..1 value noise
                float v = 0.85f + 0.30f * n;           // subtle albedo variation
                albedo[i + 0] = ToByte(baseColor.R * v);
                albedo[i + 1] = ToByte(baseColor.G * v);
                albedo[i + 2] = ToByte(baseColor.B * v);
                albedo[i + 3] = 255;
                // Gentle normal from the noise gradient (tangent space; b dominant).
                float dx = Noise(x + 1, y) - Noise(x - 1, y);
                float dy = Noise(x, y + 1) - Noise(x, y - 1);
                normal[i + 0] = ToByte(0.5f - 0.5f * dx);
                normal[i + 1] = ToByte(0.5f - 0.5f * dy);
                normal[i + 2] = 255;
                normal[i + 3] = 255;
            }
            return new TerrainMaterialLayer { AlbedoRgba = albedo, NormalRgba = normal, Tint = Color.White, TilesPerMetre = tiles, Roughness = roughness };
        }

        static byte ToByte(float f) => (byte)System.Math.Clamp((int)(f * 255f + 0.5f), 0, 255);

        // Deterministic value noise from a coordinate hash (no RNG; tileable enough for a placeholder).
        static float Noise(int x, int y)
        {
            uint h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }
    }
}
```

- [ ] **Step 6: Add the `TerrainScene3D` extensions.** In `KhaozEngine.Terrain.Render3D/TerrainScene3D.cs`, add inside the `TerrainScene3D` class (after the existing `LoadTerrainChunk`):

```csharp
        /// <summary>Realize a <see cref="TerrainLayeredMaterial"/> into a shared splat material handle (uploads the
        /// two texture arrays + mip chains + params once). Pass the handle to <see cref="LoadTerrainChunk(Scene3D,
        /// TerrainChunkMesh, Scene3D.SplatMaterialHandle)"/> or a <see cref="Scene3DChunkSink"/>.</summary>
        public static Scene3D.SplatMaterialHandle LoadTerrainMaterial(this Scene3D scene, TerrainLayeredMaterial material)
        {
            material.Validate();
            var layers = new System.Collections.Generic.List<SplatLayerImage>(material.Layers.Count);
            foreach (var l in material.Layers)
                layers.Add(new SplatLayerImage
                {
                    AlbedoRgba = l.AlbedoRgba, NormalRgba = l.NormalRgba,
                    Tint = l.Tint, TilesPerMetre = l.TilesPerMetre, Roughness = l.Roughness,
                });
            return scene.LoadSplatMaterial(material.Width, material.Height, layers,
                material.TriplanarSharpness, material.Projection, material.BaseSpecStrength);
        }

        /// <summary>Upload a chunk and draw it through the splat-terrain pipeline with <paramref name="material"/>
        /// (the baked weights are packed into the mesh's vertex colour). The textured counterpart to
        /// <see cref="LoadTerrainChunk(Scene3D, TerrainChunkMesh)"/>.</summary>
        public static MeshHandle LoadTerrainChunk(this Scene3D scene, TerrainChunkMesh chunk, Scene3D.SplatMaterialHandle material) =>
            scene.LoadMesh(TerrainSplatPacking.PackedMesh(chunk), material);
```

(`DrawTerrainChunk` is unchanged: a splat chunk is still a normal `MeshHandle`; the splat routing is baked into the mesh record, so `scene.Draw(handle, identity, white)` reaches the splat pass.)

- [ ] **Step 7: Wire the optional material into `Scene3DChunkSink`.** In `KhaozEngine.Terrain.Render3D/Scene3DChunkSink.cs`:

Add a field + ctor param. Change the ctor (line 25) to accept `Scene3D.SplatMaterialHandle material = default` (append it as the last parameter) and store it:

```csharp
        readonly Scene3D.SplatMaterialHandle _material;
```

In the ctor body, after `_propDrawRadius = propDrawRadius;`:

```csharp
            _material = material;
```

In `Load` (line 48-59), replace the `Mesh = _scene.LoadTerrainChunk(mesh),` line with:

```csharp
                Mesh = _material.IsValid ? _scene.LoadTerrainChunk(mesh, _material) : _scene.LoadTerrainChunk(mesh),
```

In `ReLod` (line 61-69), replace `load.Mesh = _scene.LoadTerrainChunk(mesh);` with:

```csharp
            load.Mesh = _material.IsValid ? _scene.LoadTerrainChunk(mesh, _material) : _scene.LoadTerrainChunk(mesh);
```

- [ ] **Step 8: Run the packing tests + the suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter TerrainSplatPackingTests`
Then: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS; no regressions.

- [ ] **Step 9: Commit**

```bash
git add KhaozEngine.Terrain.Render3D KhaozEngine.Tests/Terrain/TerrainSplatPackingTests.cs
git commit -m "terrain-render3d(terrain-pbr-splat): packing + layered material config + scene/sink wiring"
```

---

### Task 7: TerrainWalkSample textured mode (visual verification)

**Files:**
- Modify: `TerrainWalkSample/Program.cs` (build a procedural terrain material, pass its handle to the `Scene3DChunkSink`)

**Interfaces:**
- Consumes: `TerrainMaterialPresets.Procedural`, `TerrainScene3D.LoadTerrainMaterial`, `Scene3DChunkSink(..., material)` (Task 6).

- [ ] **Step 1: Read `TerrainWalkSample/Program.cs`** and locate where it constructs the `Scene3DChunkSink` and the `Scene3D`.

- [ ] **Step 2: Build + pass the material.** Where the sample creates the scene + sink, add (just before the `Scene3DChunkSink` construction):

```csharp
            // Textured terrain (PBR splat). A procedural placeholder material so the sample needs no binary assets;
            // real games wire CC0 tileable albedo/normal per layer.
            var terrainMaterial = scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural());
```

Then add `terrainMaterial` as the trailing argument to the existing `new Scene3DChunkSink(...)` call.

- [ ] **Step 3: Build the sample**

Run: `dotnet build TerrainWalkSample/TerrainWalkSample.csproj -c Debug`
Expected: build succeeds.

- [ ] **Step 4: Manual visual verification (user-run).** This is a windowed run; hand the user a one-click boot command (do NOT run it via a tool). Expected to see grass/dirt/rock/sand/snow tiled, blended by slope/height, with surface (normal-map) detail on the ground and mountains, instead of the flat colour ramp.

```bash
dotnet run --project /Users/antonio/KhaozEngine/.claude/worktrees/feature+terrain-pbr-splat/TerrainWalkSample/TerrainWalkSample.csproj -c Debug
```

- [ ] **Step 5: Commit**

```bash
git add TerrainWalkSample/Program.cs
git commit -m "sample(terrain-pbr-splat): TerrainWalkSample textured terrain mode"
```

---

### Task 8: Release ritual + full doc sweep

**Files:**
- Modify: `Directory.Build.props` (`<KhaozEngineVersion>` bump)
- Modify: `CHANGELOG.md` (new entry, newest-first)
- Modify: `docs/ROADMAP.md` (delete the shipped terrain-PBR-splat bullet of item #3; "Current released version"), `docs/CONSUMERS.md` ("Engine current version"), `README.md` (`<PackageReference>` example + package/feature notes), `CLAUDE.md` (package map: the new splat-material + terrain-material API), `docs/RENDER-PIPELINE.md` (the splat pipeline), `docs/USING-KHAOZENGINE.md` (a usage section)

- [ ] **Step 1: Re-check no concurrent release took the next version.**

Run: `git fetch && git log origin/main -1 --oneline && git tag | sort -V | tail -3 && grep KhaozEngineVersion Directory.Build.props`
Expected: confirm the current line is `7.62.0` and no `v7.63.0` tag exists. If a higher version landed, bump past it instead and use that everywhere below.

- [ ] **Step 2: Bump the version.** In `Directory.Build.props`, set `<KhaozEngineVersion>7.63.0</KhaozEngineVersion>`.

- [ ] **Step 3: Add the CHANGELOG entry.** At the top of `CHANGELOG.md`, add (one-line digest first sentence, no em-dashes):

```markdown
## 7.63.0

Terrain PBR splat-textured materials: the terrain renders five tileable PBR layers (grass/dirt/rock/sand/snow)
blended per-fragment by the baked splat weights, with world-space triplanar tiling, normal maps, mips, and
anisotropic filtering, replacing the height/slope vertex-colour ramp (opt-in; unchanged when no material is supplied).

- Render3D: a new "splat" pipeline (`SplatFrag`, sibling of the model pipeline in `ModelRenderer`, shares the frame
  UBO + instance buffer). Two `texture2DArray`s (albedo + normal, 5 layers) + a per-layer-scalar-roughness params UBO;
  the five weights ride in the existing `ModelVertex.Color` (4 packed + a 5th derived as 1 - sum), so the vertex
  format is unchanged. New public API: `SplatProjection`, `SplatLayerImage`, `SplatMaterialConfig`/`SplatParamsData`,
  `SplatMath`, `Scene3D.LoadSplatMaterial`, `Scene3D.LoadMesh(GltfMesh, SplatMaterialHandle)`,
  `Scene3D.UnloadSplatMaterial`.
- Terrain.Render3D: `TerrainSplatPacking`, `TerrainMaterialLayer`/`TerrainLayeredMaterial`, `TerrainMaterialPresets`
  (procedural placeholder), `TerrainScene3D.LoadTerrainMaterial` + the textured `LoadTerrainChunk` overload, and an
  optional material on `Scene3DChunkSink`. With no material, the ramp path is byte-identical.
- Gpu seam: `GpuSamplerFilter.Anisotropic` + `GpuSamplerDescription.MaximumAnisotropy` (trilinear fallback when the
  device lacks anisotropy), `GpuTextureDescription.Texture2DArray`, an `UpdateTexture` overload with mip/array-layer,
  and `IGpuCommandList.GenerateMipmaps`.
```

- [ ] **Step 4: Update the three guard-checked version strings.** Set to `7.63.0`: `docs/ROADMAP.md` "Current released version", `docs/CONSUMERS.md` "Engine current version", and the `README.md` `<PackageReference>` example version.

- [ ] **Step 5: Trim ROADMAP item #3.** In `docs/ROADMAP.md` item #3 ("Visual fidelity"), delete the "Terrain PBR splat" bullet (it shipped). Leave the textured-props, water, and lighting-polish bullets. Update the intro sentence so it no longer claims the terrain renders a vertex-colour ramp.

- [ ] **Step 6: Sweep the feature docs.** Update each to mention the new public API (grep first: `git grep -n "vertex-colour ramp\|TerrainRamp\|splat" -- '*.md' CLAUDE.md`):
  - `README.md`: the package-catalog feature notes for `Render3D` + `Terrain.Render3D` (splat material / textured terrain).
  - `CLAUDE.md`: the `Terrain.Render3D` description (add the splat-material mapping) and the `Render3D` notes (the splat pipeline + `LoadSplatMaterial`).
  - `docs/RENDER-PIPELINE.md`: note the terrain splat pipeline as a second model-pass pipeline (5-layer texture-array PBR, weights in vertex colour, triplanar).
  - `docs/USING-KHAOZENGINE.md`: a short usage section ("Textured terrain (PBR splat)") showing `scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural())` + passing the handle to `Scene3DChunkSink`, and that omitting it keeps the ramp.

- [ ] **Step 7: Run the doc-version guard + the full test suite.**

Run: `bash scripts/check-doc-versions.sh && dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: the guard passes (three strings == 7.63.0); tests green.

- [ ] **Step 8: Pack to local-feed.**

Run: `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`
Expected: all packages pack at 7.63.0.

- [ ] **Step 9: Commit (single release commit) and tag (HELD).**

```bash
git add Directory.Build.props CHANGELOG.md docs README.md CLAUDE.md
git commit -m "release(7.63.0): terrain PBR splat-textured materials"
git tag v7.63.0
```

Do NOT push the branch or the tag. Stop here and confirm with the user before any push (the engine holds + batches publishes). Report that 7.63.0 is built, packed to local-feed, committed, and tagged locally, ready to push when the user says so.

---

## Notes for the implementer

- The model pass is unchanged for every existing mesh: a non-splat mesh has `SplatMaterial == -1`, so the splat `continue` in the draw loop is a no-op and the golden suite must stay green. If a golden moves, stop and investigate before re-baking.
- Reuse the existing `GpuFact` device + Scene3D offscreen harness rather than inventing one; read a neighbouring GPU test first (e.g. `KhaozEngine.Tests/Gpu/CaptureToTextureGpuTests.cs`, `KhaozEngine.Tests/Gpu/SamplerModeGpuTests.cs`) to copy the exact helper names.
- Sample the two texture arrays in binding order in `SplatFrag` (Albedo then Normal). Do not reorder; it is the Metal SPIRV-Cross first-sample-order constraint.
- The splat material's two arrays + params UBO are created ONCE per `TerrainLayeredMaterial` and shared by every chunk; only the per-chunk vertex/index buffers and the `Color` repack are per-chunk.
- Keep `SplatFrag`'s lighting in sync with `ModelFrag` (key+fill+ambient+point-light+cel). If `ModelFrag`'s lighting changes later, mirror it.
```
