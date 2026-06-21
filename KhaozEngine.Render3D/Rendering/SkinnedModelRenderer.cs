using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>Draws skinned meshes into the model MRT. Reuses ModelRenderer's frame UBO (set 0) and lit
    /// fragment shader; adds a skinned vertex shader, two extra per-vertex attributes (bone indices + weights),
    /// a per-instance bone offset, and one growable read-only structured buffer (set 1) holding every skinned
    /// draw's composed bone matrices for the frame. Instances of one mesh draw in a single instanced call, each
    /// reading its own bone range via the offset.</summary>
    internal sealed class SkinnedModelRenderer : IDisposable
    {
        /// <summary>Per-instance stream for the skinned pass: the rigid InstanceData fields plus the bone offset.
        /// 64 + 16*3 + 4 = 116 bytes.</summary>
        public struct SkinnedInstanceData
        {
            public Matrix4x4 Model;     // 64
            public Vector4 Tint;        // 16
            public Vector4 Emissive;    // 16
            public Vector4 SpecParams;  // 16
            public float BoneOffset;    // 4  (base index into the bone buffer; float so it rides as a Float1 attr)
            public const uint SizeInBytes = 116;
        }

        readonly IGpuDevice _gd;
        readonly ModelRenderer _model;          // shared frame UBO + material sets come from here
        readonly IGpuResourceLayout _boneLayout; // set 1: the bone structured buffer
        readonly IGpuPipeline _pipeline;
        readonly IGpuShaderSet _shaders;

        IGpuBuffer? _instanceBuffer; uint _instanceCapacity;
        IGpuBuffer? _boneBuffer; uint _boneCapacity;        // capacity in matrices
        IGpuResourceSet? _boneSet;

        public SkinnedModelRenderer(IGpuDevice gd, GpuOutputDescription modelOutputs, ModelRenderer model)
        {
            _gd = gd; _model = model;
            var factory = gd.Factory;

            _boneLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Bones", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Vertex)));

            _shaders = factory.CreateShadersFromSpirv(ShaderSources.SkinnedModelVert, ShaderSources.ModelFrag);

            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
                new GpuVertexElement("BoneIndices", GpuVertexElementFormat.Float4),
                new GpuVertexElement("BoneWeights", GpuVertexElementFormat.Float4));

            var instanceLayout = new GpuVertexLayoutDescription(
                stride: SkinnedInstanceData.SizeInBytes,
                instanceStepRate: 1,
                elements: new[]
                {
                    new GpuVertexElement("IModel0", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel1", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel2", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel3", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("ITint", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IEmissive", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("ISpecParams", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IBoneOffset", GpuVertexElementFormat.Float1),
                });

            _pipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[]
                {
                    GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend,
                },
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                // set 0 = material (UBO + albedo + sampler), reused from ModelRenderer's layout; set 1 = bones.
                ResourceLayouts = new[] { _model.MaterialLayout, _boneLayout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout },
                Outputs = modelOutputs,
            });
        }

        /// <summary>Upload this frame's composed bone palette (every skinned draw's matrices, concatenated) into
        /// the shared structured buffer, growing it (and recreating its resource set) on demand.</summary>
        public void UploadBones(IGpuCommandList cl, ReadOnlySpan<Matrix4x4> bones)
        {
            if (bones.Length == 0) return;
            EnsureBoneCapacity((uint)bones.Length);
            cl.UpdateBuffer(_boneBuffer!, 0, bones);
        }

        void EnsureBoneCapacity(uint count)
        {
            if (_boneBuffer != null && _boneCapacity >= count) return;
            _boneBuffer?.Dispose(); _boneSet?.Dispose();
            _boneCapacity = Math.Max(count, _boneCapacity == 0 ? 64u : _boneCapacity * 2);
            _boneBuffer = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                _boneCapacity * 64u, GpuBufferUsage.StructuredBufferReadOnly, structureByteStride: 64u));
            _boneSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_boneLayout, _boneBuffer));
        }

        public void UploadInstances(IGpuCommandList cl, ReadOnlySpan<SkinnedInstanceData> instances)
        {
            if (instances.Length == 0) return;
            EnsureInstanceCapacity((uint)instances.Length);
            cl.UpdateBuffer(_instanceBuffer!, 0, instances);
        }

        void EnsureInstanceCapacity(uint count)
        {
            if (_instanceBuffer != null && _instanceCapacity >= count) return;
            _instanceBuffer?.Dispose();
            _instanceCapacity = Math.Max(count, _instanceCapacity == 0 ? 64u : _instanceCapacity * 2);
            _instanceBuffer = _gd.Factory.CreateBuffer(
                new GpuBufferDescription(_instanceCapacity * SkinnedInstanceData.SizeInBytes, GpuBufferUsage.VertexBuffer));
        }

        public void BindPass(IGpuCommandList cl)
        {
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(1, _boneSet!);   // bones are constant across the whole skinned pass
        }

        /// <summary>Draw one skinned mesh's run. Binds the mesh's material set (or the renderer's white default)
        /// at set 0; set 1 (bones) is already bound by <see cref="BindPass"/>.</summary>
        public void DrawSkinnedMeshInstanced(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
            uint instanceStart, uint instanceCount, IGpuResourceSet? materialSet)
        {
            cl.SetGraphicsResourceSet(0, materialSet ?? _model.DefaultMaterialSet);
            cl.SetVertexBuffer(0, vb);
            cl.SetVertexBuffer(1, _instanceBuffer!);
            cl.SetIndexBuffer(ib, GpuIndexFormat.UInt16);
            cl.DrawIndexed((uint)indexCount, instanceCount, 0, 0, instanceStart);
        }

        /// <summary>Group queued skinned instances by mesh handle into <paramref name="instanceData"/> (flat,
        /// mesh-contiguous) and <paramref name="runs"/> (one per unique mesh, first-seen). Each instance keeps its
        /// own bone offset. Pure + headless-testable; both lists are cleared and refilled.</summary>
        internal static void GroupSkinnedInstances(IReadOnlyList<SkinnedSceneInstances.Instance> items,
            List<SkinnedInstanceData> instanceData, List<Scene3D.SkinnedMeshRun> runs)
        {
            instanceData.Clear(); runs.Clear();
            if (items.Count == 0) return;

            for (int i = 0; i < items.Count; i++)
            {
                var mesh = items[i].Mesh;
                int slot = FindRun(runs, mesh);
                if (slot < 0) runs.Add(new Scene3D.SkinnedMeshRun(mesh, 0, 1));
                else runs[slot] = new Scene3D.SkinnedMeshRun(mesh, 0, runs[slot].Count + 1);
            }

            uint cursor = 0;
            Span<uint> writeCursor = runs.Count <= 64 ? stackalloc uint[runs.Count] : new uint[runs.Count];
            for (int r = 0; r < runs.Count; r++)
            {
                writeCursor[r] = cursor;
                runs[r] = new Scene3D.SkinnedMeshRun(runs[r].Mesh, cursor, runs[r].Count);
                cursor += runs[r].Count;
            }

            for (int i = 0; i < (int)cursor; i++) instanceData.Add(default);
            for (int i = 0; i < items.Count; i++)
            {
                var inst = items[i];
                int slot = FindRun(runs, inst.Mesh);
                uint dst = writeCursor[slot]++;
                instanceData[(int)dst] = new SkinnedInstanceData
                {
                    Model = inst.World,
                    Tint = inst.Tint,
                    Emissive = inst.Material.Emissive,
                    SpecParams = new Vector4(inst.Material.Specular, inst.Material.Shininess, 0f, 0f),
                    BoneOffset = inst.BoneOffset,
                };
            }
        }

        static int FindRun(List<Scene3D.SkinnedMeshRun> runs, SkinnedMeshHandle mesh)
        {
            for (int r = 0; r < runs.Count; r++)
                if (runs[r].Mesh.Index == mesh.Index && runs[r].Mesh.Generation == mesh.Generation) return r;
            return -1;
        }

        public void Dispose()
        {
            _pipeline.Dispose(); _shaders.Dispose(); _boneLayout.Dispose();
            _boneSet?.Dispose(); _boneBuffer?.Dispose(); _instanceBuffer?.Dispose();
        }
    }
}
