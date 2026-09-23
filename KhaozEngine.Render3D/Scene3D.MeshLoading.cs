using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D
{
    /// <summary>Rigid mesh upload and the outline-only normal stream built beside the ordinary model stream.</summary>
    public sealed partial class Scene3D
    {
        // cutoutAlbedo is the MASK mesh's albedo texture (issue #15). With a positive alphaCutoff it gets a shadow
        // cutout set, so the depth pass alpha-tests this mesh the way the colour pass does. Without either, none is
        // built and the mesh keeps the depth-only shadow pipeline.
        MeshHandle LoadMeshInternal(GltfMesh mesh, IGpuResourceSet? material,
            IGpuResourceSet? outlineMaterial = null, int splatMaterial = -1,
            float alphaCutoff = 0f, int tileGroundMaterial = -1, Vector3[]? outlineNormals = null,
            IGpuTexture? cutoutAlbedo = null)
        {
            var f = _gd.Factory;
            IGpuBuffer? vb = null, outlineNormalVb = null, ib = null;
            IGpuResourceSet? shadowCutout = null;
            try
            {
                if (alphaCutoff > 0f && cutoutAlbedo is not null)
                    shadowCutout = _model.ShadowMap.CreateCutoutMaterialSet(cutoutAlbedo);
                outlineNormals ??= OutlineNormalBuilder.Build(mesh.Vertices, mesh.Indices32);
                vb = f.CreateBuffer(new GpuBufferDescription(
                    (uint)(mesh.Vertices.Length * ModelVertex.SizeInBytes), GpuBufferUsage.VertexBuffer));
                _gd.UpdateBuffer(vb, 0, mesh.Vertices);
                outlineNormalVb = f.CreateBuffer(new GpuBufferDescription(
                    (uint)(outlineNormals.Length * sizeof(float) * 3), GpuBufferUsage.VertexBuffer));
                _gd.UpdateBuffer(outlineNormalVb, 0, outlineNormals);
                ib = CreateIndexBuffer(mesh.Indices32, mesh.IndexFormat);

                MeshBounds bounds = MeshBounds.FromVertices(mesh.Vertices);
                int index = _slots.Alloc(out int generation);
                var slot = new Mesh(vb, outlineNormalVb, ib, mesh.Indices32.Length, mesh.IndexFormat, in bounds,
                    material, outlineMaterial, splatMaterial, alphaCutoff, tileGroundMaterial, shadowCutout);
                if (index < _meshes.Count) _meshes[index] = slot;
                else _meshes.Add(slot);
                return new MeshHandle(index, generation);
            }
            catch
            {
                vb?.Dispose();
                outlineNormalVb?.Dispose();
                ib?.Dispose();
                material?.Dispose();
                outlineMaterial?.Dispose();
                shadowCutout?.Dispose();
                throw;
            }
        }

        /// <summary>Create and fill a GPU index buffer at the mesh's chosen index width.</summary>
        IGpuBuffer CreateIndexBuffer(uint[] indices32, GpuIndexFormat format)
        {
            var f = _gd.Factory;
            if (format == GpuIndexFormat.UInt32)
            {
                var ib = f.CreateBuffer(new GpuBufferDescription(
                    (uint)(indices32.Length * sizeof(uint)), GpuBufferUsage.IndexBuffer));
                _gd.UpdateBuffer(ib, 0, indices32);
                return ib;
            }
            var i16 = new ushort[indices32.Length];
            for (int i = 0; i < i16.Length; i++) i16[i] = (ushort)indices32[i];
            var ib16 = f.CreateBuffer(new GpuBufferDescription(
                (uint)(i16.Length * sizeof(ushort)), GpuBufferUsage.IndexBuffer));
            _gd.UpdateBuffer(ib16, 0, i16);
            return ib16;
        }
    }
}
