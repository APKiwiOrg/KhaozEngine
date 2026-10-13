# Third-party notices

KhaozEngine itself is proprietary (see [LICENSE](LICENSE)). This file records the third-party work its own
source is DERIVED from: approaches followed, algorithms reproduced, or code adapted. Runtime package
dependencies are not listed here (they are declared in `Directory.Packages.props` and carry their own licences
through NuGet); this is only for material that shaped engine source.

Add an entry whenever engine code follows a specific third-party implementation closely enough that the
resemblance is the point rather than a coincidence, even when nothing is copied verbatim. Name what was
adapted, not just the project.

---

## GodotOceanWaves

- **Project:** https://github.com/2Retr0/GodotOceanWaves
- **Licence:** MIT
- **Used by:** `KhaozEngine.Render3D` FFT ocean (16.3.0): `Internal/OceanSpectrum.cs`,
  `Internal/OceanComputeShaders.cs`, `Rendering/OceanFftProducer.cs`. Design rationale:
  `docs/design/FFT-OCEAN-DESIGN-2026-07-26.md`.
- **What was adapted:** the structure and parameterization of a compute-shader Tessendorf ocean, specifically
  (a) the TMA spectrum shaping - JONSWAP with the Kitaigorodskii depth attenuation, driven by wind speed, fetch
  and depth rather than a magic amplitude; (b) the mixed directional spreading, a Hasselmann `cos^2s` lobe with
  the `16 tanh(omega_p / omega) * swell^2` swell-sharpening term; (c) the octave-separated cascade approach, one
  FFT per tile size with the wave-number range split between them; and (d) the Jacobian-driven foam model, where
  the determinant of the horizontal displacement gradient injects foam that then accumulates and dissipates over
  time.
- **What is NOT from it:** no source was copied. The kernels here are a different decomposition (in-place
  decimation-in-time in workgroup shared memory, four Hermitian-packed complex fields, spectrum evolution and map
  assembly fused into the two axis passes) forced by this engine's compute seam having no cross-dispatch barrier.
  The spectrum bake is CPU-side and headless-tested; the cascade bands are a disjoint partition of wave-number
  space; the integration with the existing shading stack, the band-limiting and the Toksvig variance transfer are
  this engine's own.

Both GodotOceanWaves and this implementation follow the same public sources: Tessendorf, *Simulating Ocean
Water*, and Horvath, *Empirical Directional Wave Spectra for Computer Graphics* (2015).

The MIT licence text, as published by that project:

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of
the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
IN THE SOFTWARE.
```

---

## UnrealEngineSkyAtmosphere

- **Project:** [sebh/UnrealEngineSkyAtmosphere](https://github.com/sebh/UnrealEngineSkyAtmosphere),
  Sebastien Hillaire's Hillaire 2020 reference implementation.
- **Licence:** [MIT](https://github.com/sebh/UnrealEngineSkyAtmosphere/blob/master/LICENSE).
- **Used by:** `KhaozEngine.Render3D` physical atmosphere (20.34.0), principally
  `AtmosphereModel.Scattering.cs`, `AtmosphereModel.cs` and their GLSL mirrors in
  `Internal/ShaderSources.Atmosphere.cs`.
- **What was ported:** transport math from `IntegrateScatteredLuminance` and `NewMultiScattCS` in
  `Resources/RenderSkyRayMarching.hlsl`, including throughput-weighted analytic segment accumulation,
  source transmittance, direct ground bounce, unit isotropic response, equal-solid-angle directional averaging
  and the geometric-series multi-scattering closure. `RayleighPhase` and `CornetteShanksMiePhaseFunction` in
  `Resources/RenderSkyCommon.hlsl` supply the Rayleigh and Mie phase math. The two reference cosine sign
  inversions are combined so positive cosine denotes forward scattering.
- **Engine-specific work:** sample counts, quadratic midpoint placement, the 0.999 response clamp and stable
  zero-extinction handling follow the engine design. Reference compute threading, shared-memory reduction,
  depth/volume machinery, global illuminance, opaque shadows and debug drawing were not ported.

The copyright and MIT licence text, verbatim from the reference repository's `LICENSE`:

```text
MIT License

Copyright (c) 2020 Epic Games, Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
