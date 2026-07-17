using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using Veldrid;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>Concurrency smoke for <see cref="GpuDeviceContext"/>'s process-wide create/dispose gate: several
    /// threads each cycle CreateHeadless -> trivial device use -> Dispose repeatedly, all running at once. This is
    /// the exact configuration that aborted the Vulkan loader on Mesa lavapipe under full-suite parallelism (two
    /// threads simultaneously inside vkCreateDevice / vkGetDeviceQueue), so a green run here is the regression
    /// sentinel for that crash on the Vulkan CI leg.</summary>
    public class GpuDeviceLifecycleTests
    {
        const int ThreadCount = 4;
        const int CyclesPerThread = 5;

        [GpuFact]
        public void Concurrent_CreateHeadless_Use_Dispose_AllThreadsSucceed()
        {
            var opts = new GraphicsDeviceOptions(false, null, false, ResourceBindingModel.Improved, true, true);
            var failures = new ConcurrentBag<Exception>();

            var tasks = new Task[ThreadCount];
            for (int t = 0; t < ThreadCount; t++)
            {
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        for (int c = 0; c < CyclesPerThread; c++)
                        {
                            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless(opts);
                            IGpuDevice device = ctx.GpuDevice;

                            // Trivial device use so the created device is actually exercised, not just torn down.
                            using IGpuBuffer buf = device.Factory.CreateBuffer(
                                new GpuBufferDescription(16, GpuBufferUsage.VertexBuffer));
                            device.UpdateBuffer(buf, 0, new[] { 1f, 2f, 3f, 4f });
                            device.WaitForIdle();
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            Task.WaitAll(tasks);

            Assert.True(failures.Count == 0, $"{failures.Count} thread(s) failed: {string.Join(" | ", failures)}");
        }
    }
}
