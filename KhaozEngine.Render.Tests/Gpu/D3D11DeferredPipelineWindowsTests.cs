using System;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class D3D11DeferredPipelineWindowsTests
{
    [GpuTheory]
    [InlineData("bind")]
    [InlineData("dispose")]
    [InlineData("device loss")]
    public void WindowsPipelineBatch_ReachesEveryWorkerBeforeJoiningAndHandlesAbandonment(string action)
    {
        if (!KhaozEngineD3D11.IsPlatformSupported) return;
        BatchWindows(action);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("windows")]
    static void BatchWindows(string action)
    {
        const int count = 4;
        Vortice.Direct3D11.D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp,
            (DeviceCreationFlags)0, [FeatureLevel.Level_11_0], out ID3D11Device? created,
            out ID3D11DeviceContext? immediate).CheckError();
        using var device = created ?? throw new InvalidOperationException("WARP returned no device.");
        using var context = immediate ?? throw new InvalidOperationException("WARP returned no context.");
        using var entered = new CountdownEvent(count);
        using var release = new ManualResetEventSlim();
        var pool = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, count);
        var liveness = new DeviceLiveness();
        var creation = new D3D11CreationGate(driverConcurrentCreates: false);
        var compiles = new D3D11ShaderCompilation<byte[]>[count];
        var pipelines = new D3D11GraphicsPipeline[count];
        var failures = new ShaderValidationException[count];
        try
        {
            for (int i = 0; i < count; i++)
            {
                var failure = failures[i] = new ShaderValidationException($"program {i}: original FXC failure");
                var compile = compiles[i] = D3D11ShaderCompilation<byte[]>.Start(() =>
                {
                    entered.Signal();
                    release.Wait();
                    throw failure;
                }, scheduler: pool.ConcurrentScheduler);
                // Only the compiler and shader handles are fake. The real pipeline constructor must not read
                // any handle or bytecode until bind. A regression fails immediately instead of deadlocking.
                var shaderSet = DispatchProxy.Create<ID3D11ShaderSet, CompileJoinProxy>();
                var proxy = (CompileJoinProxy)(object)shaderSet;
                proxy.Compilation = compile;
                proxy.Release = release;
                pipelines[i] = new(device, liveness, creation, new GpuPipelineDescription
                {
                    ShaderSet = shaderSet,
                    ResourceLayouts = [],
                    VertexLayouts = [],
                    BlendAttachments = [],
                    Outputs = new(null, GpuPixelFormat.R8G8B8A8UNorm),
                    Topology = GpuPrimitiveTopology.TriangleList
                });
            }
            entered.Wait();
            if (action == "dispose") foreach (var pipeline in pipelines) pipeline.Dispose();
            if (action == "device loss") liveness.MarkDead();
            release.Set();
            for (int i = 0; i < count; i++)
            {
                if (action == "bind")
                    Assert.Same(failures[i], Assert.Throws<ShaderValidationException>(() => pipelines[i].VertexShader));
                else Assert.Throws<ObjectDisposedException>(() => pipelines[i].VertexShader);
            }
        }
        finally
        {
            release.Set();
            for (int i = 0; i < count; i++)
            {
                pipelines[i]?.Dispose();
                if (compiles[i] is { } compile)
                    Assert.Same(failures[i], Assert.Throws<ShaderValidationException>(() => compile.Join()));
            }
            pool.Complete();
            pool.Completion.GetAwaiter().GetResult();
        }
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowsPipeline_DisposeOrDeviceLossDuringJoin_DoesNotRealizeShaderHandles(bool deviceDeath)
    {
        if (!KhaozEngineD3D11.IsPlatformSupported) return;
        PendingWindows(deviceDeath);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("windows")]
    static void PendingWindows(bool deviceDeath)
    {
        Vortice.Direct3D11.D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp,
            (DeviceCreationFlags)0, [FeatureLevel.Level_11_0], out ID3D11Device? created,
            out ID3D11DeviceContext? immediate).CheckError();
        using var device = created ?? throw new InvalidOperationException("WARP returned no device.");
        using var context = immediate ?? throw new InvalidOperationException("WARP returned no context.");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var compile = D3D11ShaderCompilation<byte[]>.Start(() => { release.Wait(); return [1]; });
        var shaderSet = DispatchProxy.Create<ID3D11ShaderSet, CompileJoinProxy>();
        var proxy = (CompileJoinProxy)(object)shaderSet;
        proxy.Compilation = compile; proxy.Release = release; proxy.JoinEntered = entered;
        var liveness = new DeviceLiveness();
        using var pipeline = new D3D11GraphicsPipeline(device, liveness, new(false), new GpuPipelineDescription
        {
            ShaderSet = shaderSet,
            ResourceLayouts = [],
            VertexLayouts = [],
            BlendAttachments = [],
            Outputs = new(null, GpuPixelFormat.R8G8B8A8UNorm),
            Topology = GpuPrimitiveTopology.TriangleList
        });
        var bind = Task.Run(() =>
        {
            try { return Record.Exception(() => { _ = pipeline.VertexShader; }); }
            finally { entered.Set(); }
        });
        try
        {
            entered.Wait();
            if (deviceDeath) liveness.MarkDead();
            else pipeline.Dispose();
        }
        finally { release.Set(); }
        Assert.IsType<ObjectDisposedException>(bind.GetAwaiter().GetResult());
        Assert.Equal(0, proxy.HandleReads);
        Assert.Equal(new byte[] { 1 }, compile.Join());
    }

    // A concrete ID3D11ShaderSet fake resolves its native return types during test discovery on every OS.
    // Generate that interface implementation only inside the Windows helper, preserving the load boundary.
    public class CompileJoinProxy : DispatchProxy
    {
        internal D3D11ShaderCompilation<byte[]> Compilation = null!;
        internal ManualResetEventSlim Release = null!;
        internal ManualResetEventSlim? JoinEntered;
        internal int HandleReads;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IDisposable.Dispose)) return null;
            if (JoinEntered is null)
                Assert.True(Release.IsSet, "Pipeline construction must not join a pending shader.");
            JoinEntered?.Set();
            bool bytecode = targetMethod.Name == "get_VertexShaderBytecode";
            if (!bytecode) Interlocked.Increment(ref HandleReads);
            byte[] bytes = Compilation.Join();
            if (bytecode) return new ReadOnlyMemory<byte>(bytes);
            throw new InvalidOperationException("A disposed pipeline must not realize native shader handles.");
        }
    }
}
