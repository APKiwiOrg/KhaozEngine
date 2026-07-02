# Textured props Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let props carry real albedo/normal/roughness surface detail, with an in-repo, deterministic, asset-free textured prop for demonstration + headless tests.

**Architecture:** The prop/model shader + renderer + `Scene3D` material-upload API already support albedo/normal/roughness (the character mesh uses it). This adds the three missing links: props opt into the existing `GltfLoader.LoadWithMaterial` path (`PropLoader.LoadPropWithMaterial` + a manifest `textured` flag), primitive meshes get tangents (`MeshOps.WithTangents`) so normal maps take effect, and a procedural mossy-stone material preset (`PropMaterialPresets.Procedural`) mirrors `TerrainMaterialPresets.Procedural` so a textured prop needs no binary asset.

**Tech Stack:** C# net10.0, `KhaozEngine.Render3D`, xUnit (`KhaozEngine.Tests`), SharpGLTF (test fixtures only), Veldrid (GpuFact visual check only).

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-02-textured-props-design.md`.
- Engine is MonoGame-free; one shared `<KhaozEngineVersion>` line in `Directory.Build.props` governs all packages. No new package added.
- No new public API without a headless test in `KhaozEngine.Tests`.
- No em-dashes / semicolons in shipped prose (CHANGELOG, docs, comments).
- Every current consumer of `PropLoader.LoadProp` / `Scene3D.LoadMesh(mesh)` must keep working byte-identically (additive only).
- Release ritual (version bump + tag + pack + push) is HELD and BATCHED; confirm with the user before any push. Concurrent engine dev is in flight: at release time re-check `git tag` + `origin/main` and take the next FREE version.
- Commit subjects: `area(scope): summary` conventional style.

---

### Task 1: `MeshOps.WithTangents`

Primitive meshes (`MeshPrimitives.Box`) have UVs but zero tangents, so the shader lights them with the geometric normal and normal maps have no effect. Add a reusable tangent generator using the existing internal `TangentMath` (same Lengyel math the glTF loader uses).

**Files:**
- Modify: `KhaozEngine.Render3D/MeshOps.cs` (add `WithTangents` alongside `WithSmoothNormals`/`RecomputeFlatNormals`)
- Test: `KhaozEngine.Tests/Render3D/MeshOpsTangentTests.cs` (create)

**Interfaces:**
- Consumes: `GltfMesh` (`.Vertices`, `.Indices32`), `ModelVertex(p,n,c,uv,tangent)`, `TangentMath.FaceDirections`, `TangentMath.Resolve` (internal, same assembly).
- Produces: `public static GltfMesh MeshOps.WithTangents(GltfMesh mesh)`.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/Render3D/MeshOpsTangentTests.cs`:

```csharp
using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class MeshOpsTangentTests
    {
        [Fact]
        public void WithTangents_BoxFaces_GetOrthonormalTangentWithHandedness()
        {
            GltfMesh box = MeshOps.WithTangents(MeshPrimitives.Box(2f));

            Assert.All(box.Vertices, v =>
            {
                var t = new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z);
                Assert.True(t.Length() > 0.99f && t.Length() < 1.01f, "tangent should be unit length");
                Assert.True(MathF.Abs(v.Tangent.W) == 1f, "handedness w must be +/-1");
                Assert.True(MathF.Abs(Vector3.Dot(t, v.Normal)) < 1e-3f, "tangent must be orthogonal to normal");
            });
        }

        [Fact]
        public void WithTangents_PreservesPositionsNormalsUvsAndIndices()
        {
            GltfMesh box = MeshPrimitives.Box(1f);
            GltfMesh tan = MeshOps.WithTangents(box);

            Assert.Equal(box.Vertices.Length, tan.Vertices.Length);
            Assert.Equal(box.Indices32, tan.Indices32);
            for (int i = 0; i < box.Vertices.Length; i++)
            {
                Assert.Equal(box.Vertices[i].Position, tan.Vertices[i].Position);
                Assert.Equal(box.Vertices[i].Normal, tan.Vertices[i].Normal);
                Assert.Equal(box.Vertices[i].Uv, tan.Vertices[i].Uv);
            }
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~MeshOpsTangentTests"`
Expected: FAIL to compile (`MeshOps.WithTangents` not defined).

- [ ] **Step 3: Write minimal implementation**

Add to `KhaozEngine.Render3D/MeshOps.cs` inside the `MeshOps` class (add `using System.Numerics;` if not present):

```csharp
/// <summary>
/// Returns a copy of <paramref name="mesh"/> with a per-vertex tangent computed from its UVs + positions
/// (Lengyel accumulate then Gram-Schmidt against the normal), so a UV-mapped primitive (e.g.
/// <see cref="MeshPrimitives.Box"/>) can be normal-mapped. A vertex whose faces have no UV gradient keeps a
/// zero tangent, which the shader reads as "no TBN" (geometric normal). Positions, normals, colours, UVs and
/// indices are unchanged.
/// </summary>
public static GltfMesh WithTangents(GltfMesh mesh)
{
    if (mesh is null) throw new ArgumentNullException(nameof(mesh));
    var verts = mesh.Vertices;
    var idx = mesh.Indices32;
    var sdir = new Vector3[verts.Length];
    var tdir = new Vector3[verts.Length];

    for (int t = 0; t + 2 < idx.Length; t += 3)
    {
        int a = (int)idx[t], b = (int)idx[t + 1], c = (int)idx[t + 2];
        TangentMath.FaceDirections(
            verts[a].Position, verts[b].Position, verts[c].Position,
            verts[a].Uv, verts[b].Uv, verts[c].Uv, out Vector3 s, out Vector3 td);
        sdir[a] += s; sdir[b] += s; sdir[c] += s;
        tdir[a] += td; tdir[b] += td; tdir[c] += td;
    }

    var outVerts = new ModelVertex[verts.Length];
    for (int i = 0; i < verts.Length; i++)
    {
        ModelVertex v = verts[i];
        Vector4 tan = TangentMath.Resolve(v.Normal, sdir[i], tdir[i], null);
        outVerts[i] = new ModelVertex(v.Position, v.Normal, v.Color, v.Uv, tan);
    }
    return new GltfMesh(outVerts, (uint[])idx.Clone());
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~MeshOpsTangentTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/MeshOps.cs KhaozEngine.Tests/Render3D/MeshOpsTangentTests.cs
git commit -m "render3d: MeshOps.WithTangents for primitive normal mapping"
```

---

### Task 2: `PropMaterialPresets.Procedural`

A deterministic, asset-free mossy-stone albedo + normal, returned as a `GltfMaterialMaps` (raw RGBA, no PNG encoder needed). Mirrors `TerrainMaterialPresets.Procedural`.

**Files:**
- Create: `KhaozEngine.Render3D/PropMaterialPresets.cs`
- Test: `KhaozEngine.Tests/Render3D/PropMaterialPresetsTests.cs` (create)

**Interfaces:**
- Consumes: `GltfMaterialMaps(DecodedImage?,DecodedImage?,DecodedImage?)`, `DecodedImage(byte[],int,int)`, `KhaozEngine.Primitives.Color`.
- Produces: `public static GltfMaterialMaps PropMaterialPresets.Procedural(int size = 64, int seed = 1337)`.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/Render3D/PropMaterialPresetsTests.cs`:

```csharp
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class PropMaterialPresetsTests
    {
        [Fact]
        public void Procedural_ProducesAlbedoAndNormalOfExpectedSize()
        {
            GltfMaterialMaps maps = PropMaterialPresets.Procedural(size: 32);

            Assert.True(maps.Albedo.HasValue);
            Assert.True(maps.Normal.HasValue);
            Assert.False(maps.IsEmpty);
            Assert.Equal(32, maps.Albedo!.Value.Width);
            Assert.Equal(32, maps.Albedo!.Value.Height);
            Assert.Equal(32 * 32 * 4, maps.Albedo!.Value.Rgba.Length);
            Assert.Equal(32 * 32 * 4, maps.Normal!.Value.Rgba.Length);
        }

        [Fact]
        public void Procedural_NormalMapIsZDominant()
        {
            GltfMaterialMaps maps = PropMaterialPresets.Procedural(size: 16);
            byte[] n = maps.Normal!.Value.Rgba;
            for (int i = 0; i < n.Length; i += 4)
                Assert.True(n[i + 2] >= 200, "tangent-space normal B (z) should dominate");
        }

        [Fact]
        public void Procedural_IsDeterministic()
        {
            byte[] a = PropMaterialPresets.Procedural(size: 24).Albedo!.Value.Rgba;
            byte[] b = PropMaterialPresets.Procedural(size: 24).Albedo!.Value.Rgba;
            Assert.Equal(a, b);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropMaterialPresetsTests"`
Expected: FAIL to compile (`PropMaterialPresets` not defined).

- [ ] **Step 3: Write minimal implementation**

Create `KhaozEngine.Render3D/PropMaterialPresets.cs`:

```csharp
using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>Procedural placeholder prop materials so the in-repo sample and tests show a textured prop without
    /// shipping binary textures. Real games supply a textured glTF (baseColor/normal) read via
    /// <see cref="PropLoader.LoadPropWithMaterial"/>. Deterministic (a coordinate hash, no RNG); mirrors
    /// <c>TerrainMaterialPresets.Procedural</c> and returns raw-RGBA <see cref="GltfMaterialMaps"/> (no PNG encoder,
    /// no asset file). Upload with <see cref="Scene3D.LoadMesh(GltfMesh,GltfMaterialMaps)"/>.</summary>
    public static class PropMaterialPresets
    {
        /// <summary>A mossy-stone albedo + a gentle derived tangent-space normal, each
        /// <paramref name="size"/> x <paramref name="size"/> RGBA8. Grey stone value-noise base with green moss
        /// mottling; the normal is the albedo-noise gradient (z dominant).</summary>
        public static GltfMaterialMaps Procedural(int size = 64, int seed = 1337)
        {
            if (size < 1) size = 1;
            var albedo = new byte[size * size * 4];
            var normal = new byte[size * size * 4];
            var stone = new Color(0.42f, 0.41f, 0.39f);
            var moss = new Color(0.24f, 0.38f, 0.16f);

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                float n = Noise(x, y, seed);                 // 0..1 stone value noise
                float m = Smooth(Noise(x, y, seed + 91));     // 0..1 moss mask
                float moss01 = m > 0.6f ? (m - 0.6f) / 0.4f : 0f;
                float v = 0.8f + 0.4f * n;
                albedo[i + 0] = ToByte((stone.R * v) * (1f - moss01) + moss.R * moss01);
                albedo[i + 1] = ToByte((stone.G * v) * (1f - moss01) + moss.G * moss01);
                albedo[i + 2] = ToByte((stone.B * v) * (1f - moss01) + moss.B * moss01);
                albedo[i + 3] = 255;

                float dx = Noise(x + 1, y, seed) - Noise(x - 1, y, seed);
                float dy = Noise(x, y + 1, seed) - Noise(x, y - 1, seed);
                normal[i + 0] = ToByte(0.5f - 0.4f * dx);
                normal[i + 1] = ToByte(0.5f - 0.4f * dy);
                normal[i + 2] = 255;
                normal[i + 3] = 255;
            }

            return new GltfMaterialMaps(
                new DecodedImage(albedo, size, size),
                new DecodedImage(normal, size, size),
                null);
        }

        static byte ToByte(float f) => (byte)Math.Clamp((int)(f * 255f + 0.5f), 0, 255);
        static float Smooth(float f) => f * f * (3f - 2f * f);

        // Deterministic value noise from a coordinate hash (no RNG; tileable enough for a placeholder).
        static float Noise(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 362437);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFF) / 65535f;
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropMaterialPresetsTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/PropMaterialPresets.cs KhaozEngine.Tests/Render3D/PropMaterialPresetsTests.cs
git commit -m "render3d: PropMaterialPresets.Procedural mossy-stone albedo+normal"
```

---

### Task 3: `AssetEntry.Textured` + manifest parse

Add an opt-in `textured` flag so a manifest declares a prop should read its glTF textures. Additive: default false, existing manifests unchanged.

**Files:**
- Modify: `KhaozEngine.Render3D/Models/AssetManifest.cs` (add `Textured` to `AssetEntry`, `AssetEntry` ctor, `Dto.Entry`, and pass it in `Parse`)
- Test: `KhaozEngine.Tests/Render3D/AssetManifestTests.cs` (add two tests)

**Interfaces:**
- Consumes: existing `AssetEntry` ctor.
- Produces: `AssetEntry.Textured` (bool), JSON key `"textured"`, ctor trailing param `bool textured = false`.

- [ ] **Step 1: Write the failing test**

Add to `KhaozEngine.Tests/Render3D/AssetManifestTests.cs`:

```csharp
[Fact]
public void Parse_TexturedFlag_ReadWhenPresent()
{
    string json = @"{ ""props"": [ { ""id"": ""p"", ""file"": ""p.glb"", ""heightMeters"": 2.0, ""textured"": true } ] }";
    AssetManifest m = AssetManifest.Parse(json);
    Assert.True(m.Props[0].Textured);
}

[Fact]
public void Parse_TexturedFlag_DefaultsFalseWhenAbsent()
{
    string json = @"{ ""props"": [ { ""id"": ""p"", ""file"": ""p.glb"", ""heightMeters"": 2.0 } ] }";
    AssetManifest m = AssetManifest.Parse(json);
    Assert.False(m.Props[0].Textured);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AssetManifestTests"`
Expected: FAIL to compile (`AssetEntry.Textured` not defined).

- [ ] **Step 3: Write minimal implementation**

In `KhaozEngine.Render3D/Models/AssetManifest.cs`:

(a) Add the property to `AssetEntry` (after `CollisionProxy`, before the ctor):

```csharp
/// <summary>True when this prop's glTF ships baseColor/normal/roughness textures that should be read and
/// bound (via <see cref="PropLoader.LoadPropWithMaterial"/>). Default false: the prop renders with its flat
/// per-material base colour as before. Degrades gracefully if a flagged asset turns out to have no textures.</summary>
public bool Textured { get; }
```

(b) Add `bool textured = false` as the trailing ctor param and assign it:

```csharp
public AssetEntry(string id, string file, float heightMeters, string source, string license,
                  ColliderShape? collider = null, bool surface = false, string? heightmap = null,
                  string? collisionShape = null, string? collisionProxy = null, bool textured = false)
{
    Id = id; File = file; HeightMeters = heightMeters; Source = source; License = license; Collider = collider;
    Surface = surface; Heightmap = heightmap; CollisionShape = collisionShape; CollisionProxy = collisionProxy;
    Textured = textured;
}
```

(c) Add the Dto field (after `CollisionProxy` in `Dto.Entry`):

```csharp
[JsonPropertyName("textured")] public bool Textured { get; set; }
```

(d) Pass it in `Parse` (extend the `entries.Add(new AssetEntry(...))` call's trailing args):

```csharp
entries.Add(new AssetEntry(p.Id!, ResolveFile(p.File!, baseDir), p.HeightMeters,
                           p.Source ?? "", p.License ?? "", ParseCollider(p.Id!, p.Collider),
                           p.Surface, heightmap, collisionShape, collisionProxy, p.Textured));
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~AssetManifestTests"`
Expected: PASS (existing + 2 new).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Models/AssetManifest.cs KhaozEngine.Tests/Render3D/AssetManifestTests.cs
git commit -m "render3d: AssetEntry.Textured opt-in manifest flag"
```

---

### Task 4: `PropLoader.LoadPropWithMaterial`

Route a prop through `GltfLoader.LoadWithMaterial` and normalize the mesh exactly as `LoadProp` does, returning the decoded material maps alongside.

**Files:**
- Modify: `KhaozEngine.Render3D/Models/PropLoader.cs`
- Create: `KhaozEngine.Tests/Render3D/PropLoaderMaterialTests.cs`
- Reference (existing fixtures, same test assembly): `KhaozEngine.Tests/Render3D/GltfMaterialAutoReadTests.cs` has `WriteTexturedTriangleGlb()` / `WriteUntexturedTriangleGlb()`.

**Interfaces:**
- Consumes: `GltfLoader.LoadWithMaterial(string) -> (GltfMesh, GltfMaterialMaps)`, `PropLoader.Normalize`, `AssetEntry`.
- Produces: `public static (GltfMesh Mesh, GltfMaterialMaps Maps) PropLoader.LoadPropWithMaterial(AssetEntry entry, PropValidation? validation = null)`.

- [ ] **Step 1: Make the two glb fixture writers reusable**

In `KhaozEngine.Tests/Render3D/GltfMaterialAutoReadTests.cs`, change `WriteTexturedTriangleGlb` and `WriteUntexturedTriangleGlb` from `private`/`static` to `internal static` (leave bodies unchanged) so the new test can reuse them. If they are already `internal`, skip this step.

- [ ] **Step 2: Write the failing test**

Create `KhaozEngine.Tests/Render3D/PropLoaderMaterialTests.cs`:

```csharp
using System.IO;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class PropLoaderMaterialTests
    {
        static AssetEntry Entry(string file) =>
            new AssetEntry("p", file, heightMeters: 2f, source: "", license: "", textured: true);

        [Fact]
        public void LoadPropWithMaterial_TexturedGlb_ReturnsDecodedMaps()
        {
            string glb = GltfMaterialAutoReadTests.WriteTexturedTriangleGlb();
            try
            {
                (GltfMesh mesh, GltfMaterialMaps maps) = PropLoader.LoadPropWithMaterial(Entry(glb));
                Assert.False(maps.IsEmpty);
                Assert.True(maps.Albedo.HasValue);
                Assert.NotEmpty(mesh.Vertices);
            }
            finally { File.Delete(glb); }
        }

        [Fact]
        public void LoadPropWithMaterial_UntexturedGlb_DegradesToEmptyMaps_AndMeshMatchesLoadProp()
        {
            string glb = GltfMaterialAutoReadTests.WriteUntexturedTriangleGlb();
            try
            {
                (GltfMesh mesh, GltfMaterialMaps maps) = PropLoader.LoadPropWithMaterial(Entry(glb));
                Assert.True(maps.IsEmpty);

                GltfMesh plain = PropLoader.LoadProp(Entry(glb));
                Assert.Equal(plain.Vertices.Length, mesh.Vertices.Length);
                for (int i = 0; i < plain.Vertices.Length; i++)
                    Assert.Equal(plain.Vertices[i].Position, mesh.Vertices[i].Position);
            }
            finally { File.Delete(glb); }
        }
    }
}
```

Note: `WriteTexturedTriangleGlb`/`WriteUntexturedTriangleGlb` must produce a mesh with a measurable Y extent (so `Normalize` to 2 m succeeds). The existing triangle fixtures span Y; if a fixture is degenerate in Y, adjust its third vertex to `(0, 1, 0)` in that helper.

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropLoaderMaterialTests"`
Expected: FAIL to compile (`PropLoader.LoadPropWithMaterial` not defined).

- [ ] **Step 4: Write minimal implementation**

Add to `KhaozEngine.Render3D/Models/PropLoader.cs` (inside `PropLoader`, next to `LoadProp`):

```csharp
/// <summary>Load + normalize a manifest entry like <see cref="LoadProp"/>, AND auto-read the glTF's first
/// textured material's baseColor/normal/metallicRoughness textures (opt-in, via
/// <see cref="GltfLoader.LoadWithMaterial"/>). Upload the mesh + maps with
/// <see cref="Scene3D.LoadMesh(GltfMesh,GltfMaterialMaps)"/>. A prop whose glTF has no textures yields an
/// all-absent <see cref="GltfMaterialMaps"/> (<see cref="GltfMaterialMaps.IsEmpty"/>), never a throw, so it
/// renders exactly as <see cref="LoadProp"/>. The mesh is identical to <see cref="LoadProp"/>'s.</summary>
public static (GltfMesh Mesh, GltfMaterialMaps Maps) LoadPropWithMaterial(AssetEntry entry, PropValidation? validation = null)
{
    (GltfMesh raw, GltfMaterialMaps maps) = LoadRawWithMaterial(entry);
    GltfMesh mesh = Normalize(raw, entry.HeightMeters, validation, entry.Id);
    return (mesh, maps);
}

static (GltfMesh, GltfMaterialMaps) LoadRawWithMaterial(AssetEntry entry)
{
    try { return GltfLoader.LoadWithMaterial(entry.File); }
    catch (Exception ex)
    {
        throw new InvalidOperationException($"PropLoader could not load prop '{entry.Id}' from '{entry.File}': {ex.Message}", ex);
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PropLoaderMaterialTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render3D/Models/PropLoader.cs KhaozEngine.Tests/Render3D/PropLoaderMaterialTests.cs KhaozEngine.Tests/Render3D/GltfMaterialAutoReadTests.cs
git commit -m "render3d: PropLoader.LoadPropWithMaterial reads prop textures"
```

---

### Task 5: Interim placement in TerrainWalkSample + visual confirmation

Show a procedural textured mossy-stone block near spawn (immediate walk-up payoff + manual playtest), and confirm on-GPU that albedo + normal reach the shader via a throwaway PNG dump.

**Files:**
- Modify: `TerrainWalkSample/Program.cs`
- Temporary (NOT committed): `KhaozEngine.Tests/Gpu/_ScratchTexturedPropDump.cs`

**Interfaces:**
- Consumes: `MeshPrimitives.Box`, `MeshOps.WithTangents`, `PropMaterialPresets.Procedural`, `Scene3D.LoadMesh(GltfMesh,GltfMaterialMaps)`, `Scene3D.Draw(MeshHandle,Matrix4x4,Color)`.

- [ ] **Step 1: Add the textured block to the sample**

In `TerrainWalkSample/Program.cs`, add a field near `_platformMesh`:

```csharp
MeshHandle _texturedProp;
Matrix4x4 _texturedPropXform;
```

In `OnLoad` (near where `_platformMesh` is created, around line 170), add:

```csharp
// Textured prop demo: a procedural mossy-stone block (albedo + normal), no binary asset.
_texturedProp = sc.LoadMesh(
    MeshOps.WithTangents(MeshPrimitives.Box(1.5f)),
    PropMaterialPresets.Procedural());
float propX = 3f, propZ = 3f;
_texturedPropXform = Matrix4x4.CreateTranslation(propX, _field.Height(propX, propZ) + 0.75f, propZ);
```

(Use the sample's existing terrain-height accessor. If it is not `_field.Height(x, z)`, match the call already used for the platform / character spawn Y in this file.)

In the draw section (near line 298 where `_platformMesh` draws), add:

```csharp
scene.Draw(_texturedProp, _texturedPropXform, Color.White);
```

- [ ] **Step 2: Build the sample**

Run: `dotnet build TerrainWalkSample/TerrainWalkSample.csproj`
Expected: build succeeds.

- [ ] **Step 3: Write the throwaway GPU dump (visual confirmation)**

Create `KhaozEngine.Tests/Gpu/_ScratchTexturedPropDump.cs` following the existing `GpuFact` pattern in `KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs` (reuse its scene setup + framebuffer readback). Render `Scene3D.LoadMesh(MeshOps.WithTangents(MeshPrimitives.Box(1.5f)), PropMaterialPresets.Procedural())` lit from an angle, read back the RGBA, and write it to `bin/textured-prop.rgba` (raw width,height,bytes). Model it precisely on the readback already used by the golden tests.

- [ ] **Step 4: Run the dump and convert to PNG**

Run: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~_ScratchTexturedPropDump"`
Then convert with stdlib Python (engine has no PNG encoder):

```bash
python3 - <<'PY'
import struct, zlib, pathlib
raw = pathlib.Path("KhaozEngine.Tests/bin/Debug/net10.0/textured-prop.rgba").read_bytes()
w, h = struct.unpack("<II", raw[:8]); px = raw[8:]
def chunk(t, d): return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t+d) & 0xffffffff)
rows = b"".join(b"\x00" + px[y*w*4:(y+1)*w*4] for y in range(h))
png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b"")
pathlib.Path("/tmp/textured-prop.png").write_bytes(png)
print("wrote /tmp/textured-prop.png", w, h)
PY
```

- [ ] **Step 5: Eyeball it, then delete the scratch dump**

Read `/tmp/textured-prop.png` inline and confirm the block shows stone/moss albedo variation + normal-map relief (not a flat grey box). Then:

```bash
git rm -f --cached KhaozEngine.Tests/Gpu/_ScratchTexturedPropDump.cs 2>/dev/null; rm -f KhaozEngine.Tests/Gpu/_ScratchTexturedPropDump.cs
```

- [ ] **Step 6: Full test run + commit the sample change**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (no scratch file left).

```bash
git add TerrainWalkSample/Program.cs
git commit -m "sample(terrainwalk): procedural textured mossy-stone prop demo"
```

Manual validation handoff (give the user this one-click boot command, do NOT run it yourself):

```bash
dotnet run --project /Users/antonio/KhaozEngine/.claude/worktrees/feature+textured-props/TerrainWalkSample/TerrainWalkSample.csproj -c Debug
```

---

### Task 6: Docs sweep + release ritual (HELD, confirm before push)

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `KhaozEngine.Render3D/README.md`, `docs/USING-KHAOZENGINE.md`, `docs/ROADMAP.md`, `docs/CONSUMERS.md`, `README.md` (version-example line).

- [ ] **Step 1: Integrate concurrent work first**

```bash
git fetch
git log --oneline origin/main -1
git tag | sort -V | tail -5
```

If `origin/main` advanced past this branch's base, merge it in FIRST and resolve conflicts here (expect `<KhaozEngineVersion>` to collide):

```bash
git merge origin/main
```

Re-run `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` on the merged result.

- [ ] **Step 2: Bump the version to the next FREE value**

Read the current `<KhaozEngineVersion>` on the up-to-date `main` and pick the next unused patch/minor (this is a minor: additive public API). Set it in `Directory.Build.props`. If a concurrent chat already claimed that `vX.Y.Z` tag, take the next free one.

- [ ] **Step 3: CHANGELOG entry (same commit as the bump)**

Prepend a newest-first `CHANGELOG.md` entry, first sentence = digest, e.g.:

```markdown
## X.Y.Z

Textured props: props can now carry albedo/normal/roughness surface detail. `PropLoader.LoadPropWithMaterial`
reads a prop glTF's textures (opt-in `textured` manifest flag), `MeshOps.WithTangents` gives primitive meshes a
TBN so normal maps take effect, and `PropMaterialPresets.Procedural` generates an asset-free mossy-stone
albedo+normal for the sample and tests. No new package; additive and backward compatible.
```

- [ ] **Step 4: Update the 3 guard-checked declarations + docs**

- `docs/CONSUMERS.md` "Engine current version" -> X.Y.Z
- `docs/ROADMAP.md` "Current released version" -> X.Y.Z, and trim near-term item #3 to note textured props landed (water + shadows remain).
- `README.md` `<PackageReference>` example version -> X.Y.Z
- `KhaozEngine.Render3D/README.md`: document `PropLoader.LoadPropWithMaterial`, `MeshOps.WithTangents`, `PropMaterialPresets.Procedural`, the `textured` manifest flag.
- `docs/USING-KHAOZENGINE.md`: add a "Textured props" subsection.

Run the guard: `bash scripts/check-doc-versions.sh` (expect PASS).

- [ ] **Step 5: Grep for stale mentions**

```bash
grep -rn "LoadPropWithMaterial\|WithTangents\|PropMaterialPresets\|\"textured\"" --include=*.md . | grep -v obj
```
Confirm each doc that should mention the new API does.

- [ ] **Step 6: Pack + full test + commit**

```bash
mkdir -p local-feed
dotnet pack -c Release -o ./local-feed
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
git add -A
git commit -m "render3d(X.Y.Z): textured props (LoadPropWithMaterial, WithTangents, PropMaterialPresets)"
```

- [ ] **Step 7: STOP - confirm before tagging/pushing**

Do NOT `git tag` or `git push` yet. The engine holds + batches releases (CI publishes every `v*` tag). Report to the user that the branch is ready, tests pass, packed to `local-feed`, and ask whether to (a) merge to `main` locally and hold, or (b) tag `vX.Y.Z` + push now, or (c) batch with other in-flight work.

---

## Self-Review

**Spec coverage:**
- Goal 1 (real-glTF prop textures) -> Tasks 3 + 4. ✓
- Goal 2 (asset-free procedural textured prop) -> Tasks 1 + 2. ✓
- Goal 3 (visible payoff + PNG proof) -> Task 5. ✓
- Docs sweep / release ritual / concurrent-dev integration -> Task 6. ✓
- Non-goals (showcase, water, shadows, real Quaternius re-ingest, per-primitive multi-texture) -> not in any task. ✓

**Placeholder scan:** No TBD/TODO; every code step shows full code. The one soft spot (Task 5 terrain-height accessor + GpuFact readback) points at the exact existing pattern to copy rather than inventing an API. ✓

**Type consistency:** `WithTangents(GltfMesh)->GltfMesh`, `Procedural(int,int)->GltfMaterialMaps`, `LoadPropWithMaterial(AssetEntry,PropValidation?)->(GltfMesh,GltfMaterialMaps)`, `AssetEntry.Textured` bool, ctor trailing `textured=false` used consistently across Tasks 3-5. ✓
