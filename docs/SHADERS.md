# Shaders

Engine shaders are written in Slang and compiled at build time into one bundle per owning assembly. The
bundle carries SPIR-V, MSL and HLSL for every program plus its reflected bindings, and the runtime reads it
with `GpuShaderBundle.FromAssembly`. Game repos and players need no shader tool: the bundle ships inside the
engine assembly.

## The build step

### Opting a project in

A project opts in with one property, a path relative to the project folder:

```xml
<KhaozShaderManifest>Shaders/programs.json</KhaozShaderManifest>
```

`Directory.Build.targets` imports [`build/KhaozEngine.Shaders.targets`](../build/KhaozEngine.Shaders.targets),
which does nothing for a project without the property. With it, the project gains:

- A build-only reference to [`tools/KhaozEngine.ShaderBuild`](../tools/KhaozEngine.ShaderBuild), so the tool
  builds first. The reference sets `ReferenceOutputAssembly="false"` and `PrivateAssets="all"`, so the owning
  assembly never references the tool and its package never lists it as a dependency.
  [DEPENDENCY-SEAMS.md](DEPENDENCY-SEAMS.md#shader-build-tool-a-build-only-edge) records the edge.
- The `KhaozBuildShaders` target, which runs `dotnet exec KhaozEngine.ShaderBuild.dll --manifest <manifest>
  --out <bundle> --pins build/slangc.pins.json` before the resource and compile steps.
- The bundle at `$(IntermediateOutputPath)khaoz.shaders.json`, embedded with the logical name
  `<AssemblyName>.khaoz.shaders.json`, which is the name `GpuShaderBundle.FromAssembly` reads.

### Incrementality

`KhaozBuildShaders` declares its inputs and its one output, so a build with nothing changed skips the tool
and embeds the existing bundle. The inputs are the manifest, every `*.slang` file under the manifest's folder
(the `KhaozShaderSource` item, which a project can extend), `build/slangc.pins.json` and the tool assembly.
A tool rebuild therefore rebuilds every bundle, and a tool change runs full Product CI. The tool writes the
bundle through a rename, so MSBuild never sees a half-written output. Design-time builds skip the target, and
`dotnet clean` removes the bundle.

### Failures

The tool exits 1 when slangc or spirv-val rejects a shader and 2 on any other refusal, and either fails the
build. slangc diagnostics use the `file(line,col): error` form, so MSBuild reports them as errors at the
shader source line.

### slangc: the pin, the override and the cache

[`build/slangc.pins.json`](../build/slangc.pins.json) pins one slangc version. For each supported host
runtime identifier (`osx-arm64`, `linux-x64`, `linux-arm64`, `win-x64`) it names the release archive URL, its
SHA-256 and the executable's path inside the archive. Every bundle records the slangc version that built it.

- `SLANGC` names a slangc executable to use instead of the download. The target passes it to the tool as
  `--slangc`. The tool refuses an executable that does not report the pinned version, so an override can
  never build a bundle that another machine could not reproduce.
- Without `SLANGC`, the tool downloads the pinned archive, verifies its digest, and extracts it into the cache.
  A first build with no network fails once with a message that names `SLANGC` and the pinned version, and
  leaves no partial cache behind.
- `KHAOZ_SLANGC_CACHE` sets the cache root. The default is `khaozengine/slangc` under the user's local
  application data folder: `%LOCALAPPDATA%` on Windows, `~/Library/Application Support` on macOS and
  `$XDG_DATA_HOME` (else `~/.local/share`) on Linux.

The cache holds one folder per version and host, `<cacheRoot>/<version>/<rid>/`. A download extracts into a
`.staging-*` folder under the root and is published under `<cacheRoot>/.publish.lock`, an exclusive file lock
that also serialises repairing a damaged folder. Concurrent builds into an empty cache may each download, but
the first to take the lock publishes and the others reuse its folder, and no build reads a half-extracted
folder. A build waits up to two minutes for the lock, then fails with a message naming `SLANGC`.

A shared cache root needs working file locking. The publish lock does nothing when
`DOTNET_SYSTEM_IO_DISABLEFILELOCKING` is set, or on a filesystem without working `flock`, such as some NFS
setups. Give each machine its own cache root in those cases.

### SPIR-V validation

The tool runs `spirv-val --target-env vulkan1.3` on every SPIR-V output when it finds a validator: `SPIRV_VAL`
when set, else `spirv-val` on `PATH`. Without one it skips validation, unless `KHAOZ_REQUIRE_SPIRV_VAL=1`, which
turns a missing validator into a refusal. A `SPIRV_VAL` that names a missing file is always a refusal.

### CI

The Product CI Linux job and the `ci` workflow's build job run the tool tests, so both install the distro
`spirv-tools` package and set `KHAOZ_REQUIRE_SPIRV_VAL=1`. Those jobs and every building leg of
`cross-platform-gpu.yml` call the
[`slangc-cache`](../.github/actions/slangc-cache/action.yml) action. It points `KHAOZ_SLANGC_CACHE` at a folder
under the runner's temp directory and caches it keyed on the runner OS, architecture and the hash of
`build/slangc.pins.json`, so a pin change starts a fresh cache. In a sharded GPU leg only the primary shard
saves the cache, so a leg's shards never race to write one key. The tool tests resolve the real slangc
inside the default test filter, so those jobs need network on a cache miss.

## Loading a program

`IGpuResourceFactory.CreateShaders(GpuShaderProgram)` turns a bundled program into a shader set that a graphics
pipeline names like any other.

- Vulkan hands each stage's SPIR-V to `vkCreateShaderModule` through the device's shared module cache, so two
  programs with the same stage bytes share one module. The entry point is `main` because slangc names every
  SPIR-V entry point `main` unless `-fvk-use-entrypoint-name` is passed, and the build step never passes it.
- Metal and Direct3D 11 do not load compiled programs yet and throw `NotSupportedException`.

Graphics pipeline creation checks the program's bindings against the pipeline's resource layouts before any
native work. Every binding needs a layout at its set with an element of the same `GpuResourceKind` at its
binding index. Otherwise creation throws `ShaderValidationException` with reason `BundleLayoutMismatch`, the
program id as `Label`, and the offending `Set` and `Binding`. A program whose vertex stage asks for precise
arithmetic is refused on a Vulkan device without float controls, as a precise GLSL pair is.
