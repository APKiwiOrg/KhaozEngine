namespace KhaozEngine.Gpu
{
    /// <summary>
    /// An optional device capability: a read map of a staging texture whose proof of completion is a fence the
    /// caller already saw signal, so the map never waits on or drains the device.
    /// <see cref="IGpuDevice.Map(IGpuTexture, GpuMapMode)"/> in <see cref="GpuMapMode.Read"/> drains the whole
    /// device on native Metal and Vulkan, which would stall a frame that reads back an earlier frame's copy while
    /// later work is in flight. Test for it with a type check on the device. The three native devices implement
    /// it.
    /// </summary>
    public interface IGpuFencedReadback
    {
        /// <summary>
        /// Map <paramref name="staging"/> for reading once <paramref name="fence"/> has signaled and covers the
        /// submission that wrote it. Never waits on or drains the device. Makes the GPU's writes visible to the
        /// CPU as the backend requires (a cache invalidate on non-coherent Vulkan memory). Subresource 0, as
        /// <see cref="IGpuDevice.Map(IGpuTexture, GpuMapMode)"/> maps. Unmap with
        /// <see cref="IGpuDevice.Unmap(IGpuTexture)"/>.
        /// </summary>
        /// <param name="staging">A texture created with <see cref="GpuTextureUsage.Staging"/> on this device.</param>
        /// <param name="fence">A fence from this device, handed to the submission that wrote
        /// <paramref name="staging"/>, or to a later one on the same device.</param>
        /// <returns>The mapped view, exactly as a read map returns it.</returns>
        /// <exception cref="System.InvalidOperationException"><paramref name="fence"/> has not signaled. Nothing is
        /// mapped.</exception>
        /// <exception cref="System.ArgumentException"><paramref name="staging"/> is not a staging texture of this
        /// device, or <paramref name="fence"/> belongs to another device.</exception>
        MappedData MapForRead(IGpuTexture staging, IGpuFence fence);
    }
}
