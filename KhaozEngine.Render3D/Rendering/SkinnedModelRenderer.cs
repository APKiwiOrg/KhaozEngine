using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>GPU skinned-mesh draw path: a skinned vertex shader + a per-draw dynamic-offset bone uniform buffer.
    /// <para><b>DORMANT - not used for drawing.</b> <see cref="Scene3D"/> deforms skinned meshes on the CPU
    /// (<see cref="SkinningMath.SkinVertex"/>) and draws them through the no-bone <see cref="ModelRenderer"/>
    /// pipeline, because the GPU bone-buffer read corrupts past element 0 in the WINDOWED Veldrid/Metal
    /// swapchain-present context (extensively bisected: only <c>bones[0]</c> survives; a constant <c>bones[1]</c> or
    /// any data-dependent index reads garbage, independent of buffer type / binding / dynamic offset / submit
    /// structure; headless/fenced is clean). This class is retained for (1) its slot/instance-layout static helpers
    /// and bone-cap constants (<see cref="MaxBonesPerDraw"/>, <see cref="SlotBytes"/>, <see cref="SkinnedInstanceData"/>,
    /// <see cref="BuildInstanceData"/>) used by Scene3D and the headless tests, and (2) a revivable GPU-skinning
    /// reference for backends where the read is sound. Its instance draw path is correct headless (fenced).</para>
    /// The bone palette is a DYNAMIC-OFFSET uniform buffer: every skinned draw's bones are packed into per-draw slots
    /// in one buffer, each draw rebinding the bone set with its slot's byte offset. Each skinned instance is its own
    /// draw; instance data (model/tint) streams via a per-instance vertex buffer, rebased per draw to its element.</summary>
    internal sealed class SkinnedModelRenderer : IDisposable
    {
        /// <summary>Per-instance stream for the skinned pass: model + tint + emissive + spec. 64 + 16*3 = 112 bytes
        /// (a multiple of 16). No bone offset rides here: the bone slot is selected by a per-draw dynamic offset.</summary>
        public struct SkinnedInstanceData
        {
            public Matrix4x4 Model;     // 64
            public Vector4 Tint;        // 16
            public Vector4 Emissive;    // 16
            public Vector4 SpecParams;  // 16
            public const uint SizeInBytes = 112;
        }

        /// <summary>Max bones in one skinned mesh's palette: the per-draw dynamic-offset window (the shader's
        /// <c>bones[128]</c>). One mesh (a tentacle/limb/creature) must have at most this many bones. 128 mat4 is an
        /// 8 KiB window (under the 64 KiB uniform-buffer limit) and a multiple of 256 bytes, so every per-draw
        /// dynamic offset (slot * <see cref="SlotBytes"/>) is automatically 256-byte aligned.</summary>
        public const int MaxBonesPerDraw = 128;
        /// <summary>Byte size of one bone slot (one skinned draw's palette window). 128 * 64 = 8192 (256-aligned).</summary>
        public const uint SlotBytes = (uint)MaxBonesPerDraw * 64u;

        readonly IGpuDevice _gd;
        readonly ModelRenderer _model;           // shared frame UBO + white default material set come from here
        readonly IGpuResourceLayout _boneLayout; // set 1: the dynamic-offset bone uniform buffer
        readonly IGpuPipeline _pipeline;
        readonly IGpuShaderSet _shaders;

        IGpuBuffer? _instanceBuffer; uint _instanceCapacity;          // capacity in instances
        IGpuBuffer? _boneBuffer; uint _boneSlotCapacity;             // capacity in per-draw slots
        IGpuResourceSet? _boneSet;                                   // binds the bone buffer as a one-slot dynamic window
        // Buffers/sets replaced by a capacity grow are RETIRED here, not disposed inline: a prior frame's command
        // list may still be reading the old buffer on the GPU when this frame grows, and disposing it then is a
        // use-after-free (garbage / dropped draws on the growth frame). Geometric growth bounds this to a handful
        // of entries ever (they stop accumulating once the high-water-mark is reached); freed only in Dispose.
        readonly List<IDisposable> _retired = new();

        public SkinnedModelRenderer(IGpuDevice gd, GpuOutputDescription modelOutputs, ModelRenderer model)
        {
            _gd = gd; _model = model;
            var factory = gd.Factory;

            _boneLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Bones", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex, dynamic: true)));

            _shaders = factory.CreateShadersFromSpirv(ShaderSources.SkinnedModelVert, ShaderSources.ModelFrag);

            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
                new GpuVertexElement("BoneIndices", GpuVertexElementFormat.Float4),
                new GpuVertexElement("BoneWeights", GpuVertexElementFormat.Float4),
                new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4)); // matches SkinnedVertex (offset 80)

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
                // set 0 = material (UBO + albedo + sampler), reused from ModelRenderer's layout; set 1 = bones (dynamic).
                ResourceLayouts = new[] { _model.MaterialLayout, _boneLayout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout },
                Outputs = modelOutputs,
            });
        }

        /// <summary>Upload this frame's slot-packed bone palette: <paramref name="slots"/> is <c>slotCount *
        /// <see cref="MaxBonesPerDraw"/></c> matrices, draw i's palette at <c>i * MaxBonesPerDraw</c>. Grows the
        /// buffer (and its dynamic-window set) on demand.</summary>
        public void UploadBones(IGpuCommandList cl, ReadOnlySpan<Matrix4x4> slots)
        {
            if (slots.Length == 0) return;
            uint slotCount = (uint)(slots.Length / MaxBonesPerDraw);
            EnsureBoneCapacity(slotCount);
            cl.UpdateBuffer(_boneBuffer!, 0, slots);
        }

        void EnsureBoneCapacity(uint slotCount)
        {
            if (_boneBuffer != null && _boneSlotCapacity >= slotCount) return;
            if (_boneBuffer != null) _retired.Add(_boneBuffer);   // may still be read by an in-flight frame; retire, don't dispose
            if (_boneSet != null) _retired.Add(_boneSet);
            _boneSlotCapacity = Math.Max(slotCount, _boneSlotCapacity == 0 ? 8u : _boneSlotCapacity * 2);
            _boneBuffer = _gd.Factory.CreateBuffer(new GpuBufferDescription(_boneSlotCapacity * SlotBytes, GpuBufferUsage.UniformBuffer));
            // The set binds a single-slot WINDOW (offset 0, size SlotBytes); the per-draw offset selects the slot.
            _boneSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                _boneLayout, new GpuBufferRange(_boneBuffer, 0, SlotBytes)));
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
            if (_instanceBuffer != null) _retired.Add(_instanceBuffer);   // retire (may be in flight), don't dispose inline
            _instanceCapacity = Math.Max(count, _instanceCapacity == 0 ? 64u : _instanceCapacity * 2);
            _instanceBuffer = _gd.Factory.CreateBuffer(
                new GpuBufferDescription(_instanceCapacity * SkinnedInstanceData.SizeInBytes, GpuBufferUsage.VertexBuffer));
        }

        public void BindPass(IGpuCommandList cl) => cl.SetPipeline(_pipeline);

        /// <summary>Draw one skinned instance. Binds its material set (set 0) or the white default, the bone slot
        /// (set 1) via the per-draw dynamic offset, the geometry, and its element of the instance buffer (rebased so
        /// it reads as instance 0). One <c>instanceCount=1</c> draw.</summary>
        public void DrawSkinnedInstance(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
            GpuIndexFormat indexFormat, uint instanceIndex, uint boneSlot, IGpuResourceSet? materialSet)
        {
            cl.SetGraphicsResourceSet(0, materialSet ?? _model.DefaultMaterialSet);
            cl.SetGraphicsResourceSet(1, _boneSet!, boneSlot * SlotBytes);
            cl.SetVertexBuffer(0, vb);
            cl.SetVertexBuffer(1, _instanceBuffer!, instanceIndex * SkinnedInstanceData.SizeInBytes);
            cl.SetIndexBuffer(ib, indexFormat);
            cl.DrawIndexed((uint)indexCount, 1, 0, 0, 0);
        }

        /// <summary>Build the per-instance stream from the queued skinned draws in submission order (no grouping:
        /// each draw renders separately). Pure + headless-testable; <paramref name="instanceData"/> is cleared and
        /// refilled. Instance i maps to bone slot i.</summary>
        internal static void BuildInstanceData(IReadOnlyList<SkinnedSceneInstances.Instance> items,
            List<SkinnedInstanceData> instanceData)
        {
            instanceData.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                var inst = items[i];
                instanceData.Add(new SkinnedInstanceData
                {
                    Model = inst.World,
                    Tint = inst.Tint,
                    Emissive = inst.Material.Emissive,
                    SpecParams = new Vector4(inst.Material.Specular, inst.Material.Shininess, 0f, 0f),
                });
            }
        }

        public void Dispose()
        {
            _pipeline.Dispose(); _shaders.Dispose(); _boneLayout.Dispose();
            _boneSet?.Dispose(); _boneBuffer?.Dispose(); _instanceBuffer?.Dispose();
            foreach (var r in _retired) r.Dispose();
            _retired.Clear();
        }
    }
}
