using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The splat-terrain material family: the loaded-material list, the load/unload pair, the debug mip readback,
    /// and the per-frame splat draw partition. Moved out of Scene3D.cs whole (a pure move, nothing changed) so it
    /// sits beside its sibling in Scene3D.TileGround.cs and each ground pipeline's material handling reads in one
    /// place instead of scattered through the main file.
    /// </summary>
    public sealed partial class Scene3D
    {
        // Loaded splat-terrain materials, indexed by SplatMaterialHandle.ListIndex. Each owns its two texture
        // arrays + params UBO + resource set, shared across meshes, disposed in Dispose or UnloadSplatMaterial.
        readonly List<SplatMaterialEntry?> _splatMaterials = new();

        /// <summary>Upload a 5-layer splat-terrain material: two texture arrays (albedo + tangent-space normal, one
        /// layer per <see cref="SplatLayerImage"/>, all the same <paramref name="width"/> x <paramref name="height"/>
        /// RGBA8), with full mip chains generated, plus a params UBO (per-layer tint/tiling/roughness + triplanar
        /// sharpness + projection + base specular). Returns a handle to draw meshes through the splat pipeline. The
        /// material is owned by the scene and freed in <see cref="Dispose"/> (or <see cref="UnloadSplatMaterial"/>);
        /// it is shared across every mesh that references it (e.g. all terrain chunks).
        /// <para>NOT A MID-FRAME CALL either: the two mip chains need a command list of their own, so this
        /// refuses with <see cref="GpuNestedRecordingException"/> while a frame is recording (#424).</para></summary>
        public SplatMaterialHandle LoadSplatMaterial(int width, int height, IReadOnlyList<SplatLayerImage> layers,
            float triplanarSharpness = 8f, SplatProjection projection = SplatProjection.Triplanar, float baseSpecStrength = 0.15f,
            TerrainSamplerConfig? sampler = null)
        {
            if (layers.Count != SplatMaterialConfig.LayerCount)
                throw new ArgumentException($"a splat material needs exactly {SplatMaterialConfig.LayerCount} layers, got {layers.Count}.", nameof(layers));
            uint w = (uint)width, h = (uint)height, mips = SplatMaterialConfig.MipLevelCount(width, height);
            // Both arrays, uploaded and mipped in one transient list, and freed TOGETHER if that list is refused
            // mid-frame: two 5-layer mipped arrays are the most expensive thing a refusal could have stranded
            // (#424).
            (IGpuTexture albedo, IGpuTexture normal) =
                TextureUploads.CreateSplatArrays(_gd, w, h, mips, layers, "Scene3D.LoadSplatMaterial");

            var data = SplatMaterialConfig.BuildParams(layers, triplanarSharpness, projection, baseSpecStrength);
            // Combined UBO: frame uniforms (re-synced each frame in the splat pass) + these params appended. One
            // uniform buffer for the whole splat pipeline (Metal mis-binds a second UBO; see ModelRenderer).
            var ubo = _model.CreateSplatParamsUbo(in data);

            // A material that overrides the sampler gets its own (owned, disposed with the material); otherwise the
            // set binds the renderer's shared default sampler and nothing extra is owned here.
            IGpuSampler? ownedSampler = sampler.HasValue ? _model.CreateTerrainSampler(sampler.Value) : null;
            var set = ownedSampler is null
                ? _model.CreateSplatMaterialSet(ubo, albedo, normal)
                : _model.CreateSplatMaterialSet(ubo, albedo, normal, ownedSampler);
            _splatMaterials.Add(new SplatMaterialEntry(albedo, normal, ubo, set, ownedSampler));
            return new SplatMaterialHandle(_splatMaterials.Count - 1);
        }

        /// <summary>Free a splat-terrain material's GPU resources (its texture arrays, params UBO, resource set) and
        /// release its slot. A <c>default</c>/Invalid handle is a no-op. Meshes still referencing it must be unloaded
        /// first (they hold no reference after this). Also a no-op once <see cref="Dispose"/> has run: Dispose
        /// already freed every splat material and cleared the backing list, so a caller that still holds a handle
        /// (e.g. a world disposed after its owning scene) would otherwise index past the end of the now-empty list
        /// and get an <see cref="ArgumentOutOfRangeException"/> instead of a silent no-op.</summary>
        public void UnloadSplatMaterial(SplatMaterialHandle h)
        {
            if (!h.IsValid || h.ListIndex >= _splatMaterials.Count) return;
            var m = _splatMaterials[h.ListIndex];
            // Queued GPU work may still reference the material's arrays/UBO/set, so drain the device first.
            if (m != null) { _gd.WaitForIdle(); m.Dispose(); }
            _splatMaterials[h.ListIndex] = null;
        }

        /// <summary>Diagnostic: read one mip level (and array layer) of a splat material's ALBEDO texture array back
        /// to the CPU as packed RGBA8; <paramref name="width"/>/<paramref name="height"/> receive that mip's own
        /// dimensions. Lets a game/test verify the generated mip chain on a real device - e.g. whether a high mip is
        /// a real blurred downsample (its average colour matches mip 0, low detail) versus a copy of mip 0 (still
        /// detailed) or empty (near-black), which is how a broken GPU mip generation shows up. Requires a mappable
        /// device, and not on the per-frame path.</summary>
        public byte[] DebugReadSplatAlbedoMip(SplatMaterialHandle h, int mipLevel, int arrayLayer, out int width, out int height)
        {
            if (!h.IsValid) throw new ArgumentException("splat material handle is Invalid.", nameof(h));
            var m = _splatMaterials[h.ListIndex] ?? throw new ArgumentException("splat material is not loaded (already unloaded).", nameof(h));
            var tex = m.AlbedoArray;
            if (mipLevel < 0 || (uint)mipLevel >= tex.MipLevels)
                throw new ArgumentOutOfRangeException(nameof(mipLevel), $"mip {mipLevel} out of range (texture has {tex.MipLevels} levels).");
            if (arrayLayer < 0)
                throw new ArgumentOutOfRangeException(nameof(arrayLayer));
            width = Math.Max(1, (int)tex.Width >> mipLevel);
            height = Math.Max(1, (int)tex.Height >> mipLevel);
            return GpuReadback.ToRgbaMip(_gd, tex, (uint)mipLevel, (uint)arrayLayer, width, height);
        }

        /// <summary>Number of splat-material slots still holding a live material (loaded and not yet unloaded). For tests.</summary>
        internal int LiveSplatMaterialCount
        {
            get { int n = 0; foreach (var m in _splatMaterials) if (m != null) n++; return n; }
        }

        // Splat-terrain pass: same uploaded instance buffer, the dedicated 5-layer texture-array pipeline. Each
        // material's combined UBO holds frame + params in one buffer, so re-sync this frame's uniforms into every
        // loaded material's UBO before drawing (usually one terrain material).
        void DrawSplatRuns(IGpuCommandList cl)
        {
            for (int i = 0; i < _splatMaterials.Count; i++)
                if (_splatMaterials[i] is { } syncSm) _model.WriteFrameUniformsTo(cl, syncSm.Ubo);
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
                uint spanStart = run.Start; uint spanLen = 0;
                for (uint s = 0; s < run.Count; s++)
                {
                    if (_instanceVisible[run.Start + s]) { if (spanLen == 0) spanStart = run.Start + s; spanLen++; }
                    else if (spanLen > 0) { _model.DrawSplatMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, spanStart, spanLen, sm.Set); CountMeshDraw(mesh.IndexCount, spanLen); spanLen = 0; }
                }
                if (spanLen > 0) { _model.DrawSplatMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, spanStart, spanLen, sm.Set); CountMeshDraw(mesh.IndexCount, spanLen); }
            }
        }

        // Free every loaded splat material and clear the list. Called from Dispose.
        void DisposeSplatMaterials()
        {
            foreach (var s in _splatMaterials) s?.Dispose();
            _splatMaterials.Clear();
        }

        /// <summary>A loaded splat-terrain material: the two 5-layer texture arrays (albedo, normal), the combined
        /// frame+params UBO (frame portion re-synced each frame), and the resource set. Owned by Scene3D; shared by
        /// every mesh that uses it.</summary>
        sealed class SplatMaterialEntry
        {
            public readonly IGpuTexture AlbedoArray, NormalArray;
            public readonly IGpuBuffer Ubo;
            public readonly IGpuResourceSet Set;
            readonly IGpuSampler? _ownedSampler;   // non-null only when the material overrode the shared sampler
            public SplatMaterialEntry(IGpuTexture albedo, IGpuTexture normal, IGpuBuffer ubo, IGpuResourceSet set, IGpuSampler? ownedSampler = null)
            { AlbedoArray = albedo; NormalArray = normal; Ubo = ubo; Set = set; _ownedSampler = ownedSampler; }
            public void Dispose() { Set.Dispose(); AlbedoArray.Dispose(); NormalArray.Dispose(); Ubo.Dispose(); _ownedSampler?.Dispose(); }
        }
    }
}
