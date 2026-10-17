namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE SWAPCHAIN CAPTURE (<see cref="IGpuSwapchainCapture"/>). A blit-model back buffer is created
    /// <c>RenderTargetOutput</c>, which is already a <c>CopyResource</c> source, so the request changes nothing
    /// and the image is copyable whenever the swapchain's attachments are live. The copy is recorded by
    /// <see cref="D3D11BackBufferCopySource.RecordCopy(D3D11Swapchain, IGpuCommandList, IGpuTexture)"/>, which also
    /// refuses a list that never bound the swapchain framebuffer in its recording.
    /// </summary>
    internal sealed partial class D3D11GpuDevice : IGpuSwapchainCapture
    {
        bool _swapchainCopySourceRequested;

        /// <inheritdoc/>
        public bool SwapchainCopySourceRequested
        {
            get => _swapchain?.CopySourceRequested ?? _swapchainCopySourceRequested;
            set
            {
                _swapchainCopySourceRequested = value;
                if (_swapchain is not null) _swapchain.CopySourceRequested = value;
            }
        }

        /// <inheritdoc/>
        /// <remarks>Always true: a <c>RenderTargetOutput</c> back buffer is a copy source.</remarks>
        public bool SwapchainCopySupported => true;

        /// <inheritdoc/>
        public bool SwapchainCopyable => _swapchain?.CurrentColourView is not null;

        /// <inheritdoc/>
        public void CopySwapchainColor(IGpuCommandList commands, IGpuTexture destination)
            => D3D11BackBufferCopySource.RecordCopy(_swapchain, commands, destination);
    }
}
