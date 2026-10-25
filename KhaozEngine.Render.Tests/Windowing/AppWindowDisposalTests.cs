using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Windowing;
using KhaozEngine.Windowing.Rumble;
using Silk.NET.Input;
using Silk.NET.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

[Collection("NativeDeviceLifecycle")]
public sealed class AppWindowDisposalTests
{
    [Theory]
    [InlineData("release", false)]
    [InlineData("release", true)]
    [InlineData("query", false)]
    [InlineData("query", true)]
    [InlineData("rumble", false)]
    [InlineData("input", false)]
    [InlineData("input", true)]
    [InlineData("tap", false)]
    [InlineData("commands", false)]
    [InlineData("device", false)]
    [InlineData("window", false)]
    [InlineData("none", false)]
    public void Disposal_attempts_every_step_in_order_and_only_rethrows_submission_failures(string firstFailure, bool laterFailures)
    {
        var order = new List<string>();
        var original = new InvalidOperationException("original teardown failure");
        var later = new InvalidOperationException("later teardown failure");
        using var inner = new SubmissionTestDevice(0) { AllowCommandList = true };
        var device = new TeardownDevice(inner, () => Step("device", true));
        var context = new GpuDeviceContext(device, null, null,
            new GpuBackendSelection(device.Backend, GpuBackendSource.OsProbe, null), ownsDevice: true);
        var commands = DisposalProxy.Create<IGpuCommandList>(() => Step("commands", true));
        var frame = new Frame { Commands = commands };
        frame.BeginSubmission(device);
        Assert.True(frame.Submission.TryRetain(Consumer(), out _));
        frame.Submission.Submit(commands);

        // Bypass the native constructor, then drive the real window teardown with owned headless resources.
        var window = (AppWindow)RuntimeHelpers.GetUninitializedObject(typeof(AppWindow));
        Set("_frame", frame);
        Set("_device", device);
        Set("_gpu", context);
        Set("_cl", commands);
        Set("_rumble", DisposalProxy.Create<IRumble>(() => Step("rumble"), nameof(IRumble.StopAll)));
        Set("_input", DisposalProxy.Create<IInputContext>(() => Step("input")));
        Set("_window", DisposalProxy.Create<IWindow>(() => Step("window", true)));
        window.AddCaptureTap(new Tap(() => Step("tap", true)));
        var taps = (FrameCaptureTaps)typeof(AppWindow).GetField("_captureTaps", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

        Exception? failure = Record.Exception(window.Dispose);

        Assert.Equal(new[] { "rumble", "input", "release", "query", "tap", "commands", "device", "window" }, order);
        Assert.Equal(0, taps.Count);
        Assert.Throws<ObjectDisposedException>(() => window.AddCaptureTap(new Tap(() => { })));
        if (firstFailure is "release" or "query")
        {
            var transport = Assert.IsType<GpuDiagnosticFailureException>(failure);
            var retained = Assert.Single(transport.Failures);
            Assert.Same(original, retained.Error);
            Assert.Equal(firstFailure == "release" ? "renderer.session.dispose.submission.consumer.0.0"
                : "renderer.session.dispose.submission.pending.0.0", retained.Stage);
            Assert.Contains(firstFailure == "release" ? nameof(Release)
                : nameof(Query), retained.Error.StackTrace);
        }
        else Assert.Null(failure);

        void Set(string field, object value) =>
            typeof(AppWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

        void Step(string name, bool failLater = false)
        {
            order.Add(name);
            if (firstFailure == name) throw original;
            if (failLater && laterFailures) throw later;
        }

        bool Query()
        {
            order.Add("query");
            if (firstFailure == "query") throw original;
            return false;
        }

        void Release()
        {
            order.Add("release");
            if (firstFailure == "release") throw original;
        }

        ThrowingConsumer Consumer() => new(Release, Query);
    }

    sealed class ThrowingConsumer(Action release, Func<bool> query) : IGpuFrameCompletionConsumer
    {
        public bool HasPending(IGpuFence completion) => query();
        public void ReleaseCompleted(IGpuFence completion) => release();
        public void DiscardUnsubmitted(IGpuFence completion) { }
    }

    sealed class Tap(Action detach) : IFrameCaptureTap
    {
        public void Record(FrameCaptureContext context) { }
        public void AfterSubmit(FrameCaptureContext context) { }
        public void Detach(Action<IDisposable> retire) => detach();
    }

    public class DisposalProxy : DispatchProxy
    {
        Action dispose = null!;
        string method = null!;

        internal static T Create<T>(Action dispose, string method = nameof(IDisposable.Dispose)) where T : class
        {
            T result = Create<T, DisposalProxy>();
            ((DisposalProxy)(object)result).dispose = dispose;
            ((DisposalProxy)(object)result).method = method;
            return result;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != method) throw new NotSupportedException(targetMethod?.Name);
            dispose();
            return null;
        }
    }

    sealed class TeardownDevice(SubmissionTestDevice inner, Action dispose) : IGpuDevice
    {
        public GpuBackendKind Backend => inner.Backend;
        public GpuCapabilities Capabilities => inner.Capabilities;
        public IGpuResourceFactory Factory => inner.Factory;
        public IGpuFramebuffer? SwapchainFramebuffer => inner.SwapchainFramebuffer;
        public IGpuSampler PointSampler => inner.PointSampler;
        public IGpuSampler LinearSampler => inner.LinearSampler;
        public bool SyncToVerticalBlank { get => inner.SyncToVerticalBlank; set => inner.SyncToVerticalBlank = value; }
        public void Submit(IGpuCommandList commands) => inner.Submit(commands);
        public void Submit(IGpuCommandList commands, IGpuFence fence) => inner.Submit(commands, fence);
        public void Submit(IGpuCommandList commands, ReadOnlySpan<IGpuFence> completions) => inner.Submit(commands, completions);
        public void WaitForIdle() => inner.WaitForIdle();
        public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, ReadOnlySpan<T> values) where T : unmanaged => inner.UpdateBuffer(buffer, offset, values);
        public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, T[] values) where T : unmanaged => inner.UpdateBuffer(buffer, offset, values);
        public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, in T value) where T : unmanaged => inner.UpdateBuffer(buffer, offset, in value);
        public void UpdateTexture(IGpuTexture texture, byte[] values, uint x, uint y, uint width, uint height) => inner.UpdateTexture(texture, values, x, y, width, height);
        public void UpdateTexture(IGpuTexture texture, byte[] values, uint x, uint y, uint width, uint height, uint mip, uint layer) => inner.UpdateTexture(texture, values, x, y, width, height, mip, layer);
        public MappedData Map(IGpuTexture texture, GpuMapMode mode) => inner.Map(texture, mode);
        public MappedData Map(IGpuBuffer buffer, GpuMapMode mode) => inner.Map(buffer, mode);
        public void Unmap(IGpuTexture texture) => inner.Unmap(texture);
        public void Unmap(IGpuBuffer buffer) => inner.Unmap(buffer);
        public void ResizeSwapchain(uint width, uint height) => inner.ResizeSwapchain(width, height);
        public void Present() => inner.Present();
        public void Dispose() => dispose();
    }
}
