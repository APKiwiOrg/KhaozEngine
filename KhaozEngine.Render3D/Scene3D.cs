using System;
using System.Collections.Generic;
using System.Numerics;
using Veldrid;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A drawable 3D scene: an <see cref="IsoCamera3D"/>, a set of uploaded meshes, a per-frame instance queue,
    /// and the pixel post chain. Load meshes once with <see cref="LoadMesh"/>; each frame call
    /// <see cref="Begin"/>, queue instances with <see cref="Draw"/>, then have the surface/host render. Owns its
    /// Veldrid resources but records into a caller-supplied command list (see <see cref="Render3DSurface"/> /
    /// <see cref="Render3DHost"/>); the public surface stays Veldrid-free.
    /// </summary>
    public sealed class Scene3D : IDisposable
    {
        readonly GraphicsDevice _gd;
        readonly OutputDescription _targetOutput;
        readonly ModelRenderer _model;
        readonly PixelPostProcess _post;
        readonly RenderResources _res;
        readonly List<Mesh> _meshes = new();
        readonly SceneInstances _instances = new();

        public IsoCamera3D Camera { get; } = new();
        public PixelPostProcessSettings Post { get; } = new();

        internal Scene3D(GraphicsDevice gd, OutputDescription targetOutput)
        {
            _gd = gd;
            _targetOutput = targetOutput;
            _res = new RenderResources(gd, Post.RenderWidth, Post.RenderHeight);
            _model = new ModelRenderer(gd, _res.ModelFB.OutputDescription);
            _post = new PixelPostProcess(gd, _res.PingAFB.OutputDescription, targetOutput);
            _post.BindTargets(_res);
        }

        /// <summary>Upload a loaded mesh to the GPU once; returns a handle to instance it with <see cref="Draw"/>.</summary>
        public MeshHandle LoadMesh(GltfMesh mesh)
        {
            var f = _gd.ResourceFactory;
            var vb = f.CreateBuffer(new BufferDescription((uint)(mesh.Vertices.Length * ModelVertex.SizeInBytes), BufferUsage.VertexBuffer));
            _gd.UpdateBuffer(vb, 0, mesh.Vertices);
            var ib = f.CreateBuffer(new BufferDescription((uint)(mesh.Indices.Length * sizeof(ushort)), BufferUsage.IndexBuffer));
            _gd.UpdateBuffer(ib, 0, mesh.Indices);
            _meshes.Add(new Mesh(vb, ib, mesh.Indices.Length));
            return new MeshHandle(_meshes.Count - 1);
        }

        /// <summary>Start a frame: clear the instance queue. Call before submitting instances.</summary>
        public void Begin() => _instances.Begin();

        /// <summary>Queue one instance: draw <paramref name="mesh"/> at world transform <paramref name="world"/> (no tint).</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world) => _instances.Add(mesh, world, Vector4.One);

        /// <summary>Queue one instance with a per-instance RGBA <paramref name="tint"/> that multiplies the lit color.</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Vector4 tint) => _instances.Add(mesh, world, tint);

        /// <summary>Queue one instance with a per-instance <paramref name="tint"/> and <paramref name="material"/>
        /// (emissive glow + specular).</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Vector4 tint, Material material) => _instances.Add(mesh, world, tint, material);

        void EnsureSize(int viewportW, int viewportH)
        {
            if (_res.Width != Post.RenderWidth || _res.Height != Post.RenderHeight)
            {
                _res.Resize(Post.RenderWidth, Post.RenderHeight);
                _post.BindTargets(_res);
            }
            Camera.AspectRatio = viewportH > 0 ? (float)viewportW / viewportH : Camera.AspectRatio;
        }

        /// <summary>
        /// Record the scene (model pass over all queued instances -> post chain -> blit) into
        /// <paramref name="cl"/>, ending on <paramref name="target"/>. The caller owns Begin/End/Submit of
        /// <paramref name="cl"/>. <paramref name="viewportW"/>/<paramref name="viewportH"/> are the target size.
        /// </summary>
        internal void RenderInternal(CommandList cl, int viewportW, int viewportH, Framebuffer target)
        {
            EnsureSize(viewportW, viewportH);
            _post.PrepareUniforms(cl, _res, Post);

            _model.BeginModelPass(cl, _res, Post);
            Matrix4x4 vp = Camera.ViewProjection;
            Vector3 eye = Camera.Eye;
            foreach (var inst in _instances.Items)
            {
                var m = _meshes[inst.Mesh.Index];
                _model.DrawInstance(cl, m.Vb, m.Ib, m.IndexCount, vp, inst.World, Post, inst.Tint, eye, inst.Material);
            }

            _post.Run(cl, _res, target, Post);
        }

        public void Dispose()
        {
            _model.Dispose();
            _post.Dispose();
            _res.Dispose();
            foreach (var m in _meshes) { m.Vb.Dispose(); m.Ib.Dispose(); }
        }

        readonly struct Mesh
        {
            public readonly DeviceBuffer Vb, Ib;
            public readonly int IndexCount;
            public Mesh(DeviceBuffer vb, DeviceBuffer ib, int indexCount) { Vb = vb; Ib = ib; IndexCount = indexCount; }
        }
    }
}
