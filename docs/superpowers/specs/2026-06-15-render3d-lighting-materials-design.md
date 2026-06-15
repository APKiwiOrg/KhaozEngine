# Render3D lighting + materials design (5.19.0-experimental)

**Goal:** upgrade the Render3D model pass from a single hardcoded-ish directional light to a richer, still
cheap lighting model — a key light + a fill light + Blinn-Phong specular — plus per-instance **materials**
(emissive self-illumination, specular strength, shininess). Makes 3D scenes (Hardpoint's board, any game) read
with real form and lets specific things glow (projectiles) or shine (metal). Snapshot-verifiable on Metal.

## Current state
The model shader (`Internal/ShaderSources.cs` `ModelVert`/`ModelFrag`) does one directional light:
`lit = vColor.rgb * Tint.rgb * (Ambient + LightColor * ndl)` with optional cel banding (`Params.x`). The UBO
(`ModelRenderer.Ubo`, 208 bytes) carries `ViewProj, Model, Dir, Color, Ambient, Params, Tint`. Light params
come from `PixelPostProcessSettings` (`LightDirection`, `LightColor`, `AmbientColor`, `CelBands`). Per-instance
data is just `Tint` (`SceneInstances.Instance`, `Scene3D.Draw(mesh, world, tint)`). The camera eye is available
(`IsoCamera3D.Eye`) but not passed to the shader.

## Changes

### 1. Global lighting — `PixelPostProcessSettings` (additive)
Add a fill light (defaults give a subtle cool fill from roughly the opposite/other side so forms aren't flat):
- `Vector3 FillLightDirection = new(0.6f, -0.3f, 0.5f);` (travel direction; will be normalized)
- `Vector4 FillLightColor = new(0.20f, 0.24f, 0.34f, 1f);` (dim cool)
Keep the existing `LightDirection`/`LightColor`/`AmbientColor`/`CelBands` (the key light). Specular **colour**
comes from the key `LightColor`; specular **amount** is per-material (below), so default look is unchanged
when materials are default and FillLightColor stays dim.

### 2. Per-instance material — new `Material` struct (public)
```csharp
namespace KhaozEngine.Render3D;
public readonly struct Material
{
    public Vector4 Emissive { get; }   // self-illumination added after lighting (default zero = none)
    public float Specular { get; }     // Blinn-Phong specular strength 0..1 (default 0 = matte)
    public float Shininess { get; }    // specular exponent (default 32)
    public Material(Vector4 emissive, float specular, float shininess);
    public static Material None { get; } // emissive 0, specular 0, shininess 32 — the current matte look
    public static Material Emissive(Vector4 color);          // glow, no specular
    public static Material Shiny(float specular, float shininess = 48f); // specular, no glow
}
```

### 3. `Scene3D` / `SceneInstances` (additive overloads)
- `SceneInstances.Instance` gains a `Material Material` field; existing ctor defaults it to `Material.None`,
  add a ctor overload taking the material.
- `Scene3D.Draw(mesh, world)` and `Draw(mesh, world, tint)` keep current behaviour (material = `Material.None`).
- New `Scene3D.Draw(mesh, world, tint, Material material)`.
- `Scene3D.RenderInternal` passes the camera eye position (`Camera.Eye`) + each instance's material into
  `ModelRenderer.DrawInstance`.

### 4. `MeshInstance` ECS component (additive) + binder
- `MeshInstance` gains a `Material Material` field (default `Material.None` so existing entities are unchanged).
- `Scene3DBinder.Submit(world, scene)` draws with that material (the `Action` overload signature is unchanged;
  the scene-targeting overload reads `MeshInstance.Material`). Keep the headless `Submit(world, Action<...>)`
  working; if the action can't carry material, leave that overload tint-only and document it.

### 5. `ModelRenderer` UBO + upload
Grow `Ubo` to add: `Vector4 FillDir; Vector4 FillColor; Vector4 CameraPos; Vector4 Emissive; Vector4 SpecParams;`
(SpecParams.x = specular strength, .y = shininess). New size = 2 mat4 (128) + 10 vec4 (160) = **288 bytes**;
update the `CreateBuffer(288, ...)` and the `// 2 mat4 + N vec4` comment. `DrawInstance` signature gains
`Vector3 cameraPos` and `Material material`; populate the new fields (`FillDir` normalized, `CameraPos` as
`vec4(eye,1)`).

### 6. Shaders (`ShaderSources.cs`)
- `ModelVert`: add `layout(location=3) out vec3 vWorldPos;` and set `vWorldPos = world.xyz;`. Mirror the new
  UBO field order EXACTLY in both stages (the combined UBO must match the C# struct layout / std140).
- `ModelFrag`: add the matching UBO fields + `in vec3 vWorldPos`. Lighting:
  ```
  vec3 N = normalize(vNormalW);
  vec3 albedo = vColor.rgb * Tint.rgb;
  float ndlKey  = max(dot(N, -normalize(LightDir.xyz)), 0.0);
  float ndlFill = max(dot(N, -normalize(FillDir.xyz)), 0.0);
  float bands = Params.x;
  if (bands >= 1.0) { ndlKey = floor(ndlKey*bands+0.5)/bands; ndlFill = floor(ndlFill*bands+0.5)/bands; }
  vec3 diffuse = LightColor.rgb*ndlKey + FillColor.rgb*ndlFill;
  // Blinn-Phong specular from the key light only, gated by key ndl so back faces don't shine.
  vec3 V = normalize(CameraPos.xyz - vWorldPos);
  vec3 H = normalize(-normalize(LightDir.xyz) + V);
  float spec = pow(max(dot(N,H),0.0), max(SpecParams.y,1.0)) * SpecParams.x * step(0.0001, ndlKey);
  vec3 lit = albedo * (Ambient.rgb + diffuse) + LightColor.rgb*spec + Emissive.rgb;
  oColor = vec4(lit, 1.0);
  ```
  Keep `oNormal`/`oDepth` writes as-is (still feed the edge pass).

## Files
- Modify `KhaozEngine.Render3D/PixelPostProcessSettings.cs` (fill light fields).
- Create `KhaozEngine.Render3D/Material.cs`.
- Modify `KhaozEngine.Render3D/SceneInstances.cs`, `Scene3D.cs` (material plumbing + Draw overload + eye pass).
- Modify `KhaozEngine.Render3D/Rendering/ModelRenderer.cs` (UBO struct/size, DrawInstance signature, upload).
- Modify `KhaozEngine.Render3D/Internal/ShaderSources.cs` (vert worldPos out; frag two-light + specular + emissive).
- Modify the `MeshInstance` component + `Scene3DBinder` (additive material).
- Tests: extend `KhaozEngine.Tests/Render3D/SceneInstancesTests.cs` + `Scene3DBinderTests.cs`.
- Release: bump `<KhaozEngine5xVersion>` 5.18.0 -> 5.19.0-experimental, CHANGELOG, pack 5 pkgs.

## Testing
- Headless (no GPU): `Material.None`/`Emissive`/`Shiny` defaults; `Scene3D.Draw(mesh,world,tint,material)`
  records the material in `SceneInstances` (use the existing instance-recording test pattern); `MeshInstance`
  default material is `None`; `Scene3DBinder.Submit` carries the material through.
- The shader math can't be unit-tested; the controller verifies on screen via `Render3DSnapshot`: a row of
  spheres — matte vs shiny (specular highlight visible), an emissive one (glows in shadow), and the fill light
  softening the dark side — asymmetric scene, per the upside-down lesson.
- Default look (all `Material.None`, dim fill) must stay close to today's; the snapshot confirms no regression.
