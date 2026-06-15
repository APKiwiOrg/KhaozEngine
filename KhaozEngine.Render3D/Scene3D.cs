using System;
using System.Numerics;
using Veldrid;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A drawable retro-3D scene: an <see cref="IsoCamera3D"/>, one model, and a pixel post chain.
    /// Owns the internal Veldrid resources; the public surface is Veldrid-free. Created by
    /// <see cref="Render3DHost"/>; consumers drive it through <see cref="Camera"/>, <see cref="Post"/>,
    /// <see cref="LoadModel"/> and <see cref="Spin"/>.
    /// </summary>
    public sealed class Scene3D : IDisposable
    {
        readonly GraphicsDevice _gd;
        readonly CommandList _cl;
        readonly OutputDescription _swapchainOutput;
        ModelRenderer _model;
        PixelPostProcess _post;
        RenderResources _res;
        DeviceBuffer? _vb, _ib;
        int _indexCount;
        float _spin;

        /// <summary>The orthographic iso camera. Tweak Azimuth/Elevation/OrthoSize/Zoom freely.</summary>
        public IsoCamera3D Camera { get; } = new();

        /// <summary>Pixel post-process toggles + palette + low-res size.</summary>
        public PixelPostProcessSettings Post { get; } = new();

        internal Scene3D(GraphicsDevice gd, OutputDescription swapchainOutput)
        {
            _gd = gd;
            _swapchainOutput = swapchainOutput;
            _cl = gd.ResourceFactory.CreateCommandList();
            _res = new RenderResources(gd, Post.RenderWidth, Post.RenderHeight);
            _model = new ModelRenderer(gd, _res.ModelFB.OutputDescription);
            _post = new PixelPostProcess(gd, _res.PingAFB.OutputDescription, swapchainOutput);
            _post.BindTargets(_res);
        }

        /// <summary>Upload a loaded mesh to the GPU (replaces any previous model).</summary>
        public void LoadModel(GltfMesh mesh)
        {
            _vb?.Dispose(); _ib?.Dispose();
            var f = _gd.ResourceFactory;
            _vb = f.CreateBuffer(new BufferDescription((uint)(mesh.Vertices.Length * ModelVertex.SizeInBytes), BufferUsage.VertexBuffer));
            _gd.UpdateBuffer(_vb, 0, mesh.Vertices);
            _ib = f.CreateBuffer(new BufferDescription((uint)(mesh.Indices.Length * sizeof(ushort)), BufferUsage.IndexBuffer));
            _gd.UpdateBuffer(_ib, 0, mesh.Indices);
            _indexCount = mesh.Indices.Length;
        }

        /// <summary>Advance the model's spin by dt seconds.</summary>
        public void Spin(float dt) => _spin += dt * 0.6f;

        void EnsureSize()
        {
            if (_res.Width == Post.RenderWidth && _res.Height == Post.RenderHeight) return;
            _res.Resize(Post.RenderWidth, Post.RenderHeight);
            _post.BindTargets(_res);
        }

        /// <summary>Render lit model -> low-res RT -> post chain -> the host's swapchain framebuffer.</summary>
        internal void RenderInternal(Framebuffer swapchainFB)
        {
            EnsureSize();
            Camera.AspectRatio = (float)Post.RenderWidth / Post.RenderHeight;

            _cl.Begin();
            _post.PrepareUniforms(_cl, _res, Post);

            if (_vb != null && _ib != null)
            {
                var modelM = Matrix4x4.CreateRotationY(_spin) * Matrix4x4.CreateRotationX(0.15f);
                _model.Draw(_cl, _vb, _ib, _indexCount, Camera.ViewProjection, modelM, _res, Post);
            }
            else
            {
                _cl.SetFramebuffer(_res.ModelFB);
                _cl.ClearColorTarget(0, new RgbaFloat(Post.BackgroundColor.X, Post.BackgroundColor.Y, Post.BackgroundColor.Z, 1f));
                _cl.ClearColorTarget(1, new RgbaFloat(0.5f, 0.5f, 0.5f, 1f));
                _cl.ClearColorTarget(2, new RgbaFloat(1f, 1f, 1f, 1f));
                _cl.ClearDepthStencil(1f);
            }

            _post.Run(_cl, _res, swapchainFB, Post);
            _cl.End();
            _gd.SubmitCommands(_cl);
        }

        public void Dispose()
        {
            _cl.Dispose();
            _model.Dispose();
            _post.Dispose();
            _res.Dispose();
            _vb?.Dispose(); _ib?.Dispose();
        }
    }
}
