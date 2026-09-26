using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// One headless device, a <see cref="TemporalResolveRenderer"/> and its <see cref="TemporalHistory"/>, with the
    /// scene inputs the resolve reads uploaded per frame, for the temporal resolve's GPU facts. Every texel starts at
    /// one scene depth, and <see cref="FillDepth"/> replaces it for a scene with more than one surface.
    /// </summary>
    internal class TemporalResolveRig : IDisposable
    {
        readonly GpuDeviceContext _gpu;
        readonly IGpuDevice _gd;
        readonly TemporalResolveRenderer _renderer;
        readonly TemporalHistory _history = new();
        readonly IGpuTexture _scene, _opaque, _depth, _motion;
        readonly int _width, _height;
        long _frame;

        public TemporalResolveRig(int internalWidth, int internalHeight, int displayWidth, int displayHeight, float sceneNdc)
        {
            _width = internalWidth;
            _height = internalHeight;
            _gpu = GpuDeviceContext.CreateHeadless();
            _gd = _gpu.GpuDevice;
            _renderer = new TemporalResolveRenderer(_gd);
            _history.EnsureTargets(_gd, displayWidth, displayHeight, internalWidth, internalHeight);
            _scene = Input(GpuPixelFormat.R16G16B16A16Float);
            _opaque = Input(GpuPixelFormat.R16G16B16A16Float);
            _depth = Input(GpuPixelFormat.R32Float);
            _motion = Input(GpuPixelFormat.R16G16Float);
            var ndc = new float[internalWidth * internalHeight];
            Array.Fill(ndc, sceneNdc);
            TemporalTextureIo.Upload(_gd, _depth, ndc);
        }

        IGpuTexture Input(GpuPixelFormat format) => _gd.Factory.CreateTexture(
            GpuTextureDescription.Texture2D((uint)_width, (uint)_height, format, GpuTextureUsage.Sampled));

        /// <summary>Choose this frame's pair, as the scene does once per frame index.</summary>
        public void BeginFrame() => _history.BeginResolve(_frame++);

        /// <summary>Move the history to a new display size at the same internal size, as a resize with a fixed render
        /// size does. The scene inputs and their generation stay, so only the history's target generation tells the
        /// renderer its sets are stale. True when the targets were recreated.</summary>
        public bool ResizeDisplay(int displayWidth, int displayHeight)
            => _history.EnsureTargets(_gd, displayWidth, displayHeight, _width, _height);

        public void Fill(float[] scene, float[] opaque, float[] motion)
        {
            TemporalTextureIo.Upload(_gd, _scene, scene);
            TemporalTextureIo.Upload(_gd, _opaque, opaque);
            TemporalTextureIo.Upload(_gd, _motion, motion);
        }

        /// <summary>This frame's NDC depth per internal texel, rows top to bottom.</summary>
        public void FillDepth(float[] ndc) => TemporalTextureIo.Upload(_gd, _depth, ndc);

        public void FillHistory(float[] color, float[] state, float previousDepth)
            => FillHistory(color, state, (_, _) => previousDepth);

        public void FillHistory(float[] color, float[] state, Func<int, int, float> previousDepth)
        {
            TemporalTextureIo.Upload(_gd, _history.Color(_history.ReadIndex), color);
            TemporalTextureIo.Upload(_gd, _history.Confidence(_history.ReadIndex), state);
            var depth = new float[_width * _height];
            for (int y = 0; y < _height; y++)
                for (int x = 0; x < _width; x++)
                    depth[y * _width + x] = previousDepth(x, y);
            TemporalTextureIo.Upload(_gd, _history.PreviousDepth(_history.ReadIndex), depth);
        }

        /// <summary>Record and run one resolve and depth store. The store takes the resolve's own depth
        /// parameters, which is what <see cref="TemporalResolveMath.BuildDepthStore"/> gives for the same
        /// projection.</summary>
        public void Resolve(in TemporalResolveUniforms uniforms)
        {
            _renderer.BindInputs(new TemporalResolveInputs(_scene, _opaque, _depth, _motion, 1), _history);
            using IGpuCommandList cl = _gd.Factory.CreateCommandList();
            using (GpuRecording.Open(_gd, cl, nameof(TemporalResolveRig)))
            {
                _renderer.PrepareUniforms(cl, uniforms, new TemporalDepthStoreUniforms { CurrentDepth = uniforms.CurrentDepth });
                _renderer.Run(cl, _history);
            }
            _gd.Submit(cl);
            _gd.WaitForIdle();
            Assert.NotNull(_renderer.CurrentSet);
        }

        public float[] ReadColor() => TemporalTextureIo.Read(_gd, _history.Color(_history.WriteIndex));
        public float[] ReadState() => TemporalTextureIo.Read(_gd, _history.Confidence(_history.WriteIndex));
        public float[] ReadPreviousDepth() => TemporalTextureIo.Read(_gd, _history.PreviousDepth(_history.WriteIndex));

        public void Dispose()
        {
            _renderer.Dispose();
            _history.ReleaseTargets();
            _scene.Dispose();
            _opaque.Dispose();
            _depth.Dispose();
            _motion.Dispose();
            _gpu.Dispose();
        }
    }
}
