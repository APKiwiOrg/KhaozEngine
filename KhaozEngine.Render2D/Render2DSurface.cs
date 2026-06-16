using System;
using KhaozEngine.Render2D.Internal;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// A 2D drawing surface bound to a <see cref="AppWindow"/> (from KhaozEngine.Windowing): it builds a
    /// <see cref="SpriteBatch"/> + texture/font loaders on the window's GPU device, so you draw a 2D scene
    /// into the window's frames. The window owns the device and the frame loop; this just renders into it.
    /// </summary>
    public sealed class Render2DSurface : IDisposable
    {
        readonly Render2DCore _core;

        public Render2DSurface(AppWindow window)
        {
            // borrow the window's GPU device (it owns it); don't dispose it here.
            _core = new Render2DCore(window.GpuDevice, window.GpuDevice.SwapchainFramebuffer!.Outputs, ownsDevice: false);
        }

        public SpriteBatch Batch => _core.Batch;
        public Texture2D LoadTexture(string pngPath) => _core.LoadTexture(pngPath);
        public Texture2D CreateTexture(byte[] rgba, int width, int height) => _core.CreateTexture(rgba, width, height);
        public SpriteFont LoadFont(string ttfPath, float pixelHeight) => _core.LoadFont(ttfPath, pixelHeight);

        /// <summary>Bind this frame's command list/viewport to the batch. Call once per frame before drawing.</summary>
        public void NewFrame(Frame frame) => _core.Batch.NewFrame(frame.Commands, frame.Width, frame.Height);

        public void Dispose() => _core.Dispose();
    }
}
