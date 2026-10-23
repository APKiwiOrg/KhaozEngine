using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Metal.Diagnostics;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("AllocSensitive")]
public sealed partial class MetalFramePoolProbeTests
{
    [Fact]
    public void Default_and_disabled_scopes_preserve_nested_standalone_native_sequence()
    {
        using var native = new Native();
        var before = MetalFramePoolProbe.Counters;
        using (default(MetalFramePoolProbe.Scope)) { }
        using (MetalFramePoolProbe.Enter(false))
        {
            using var a = ObjCAutoreleasePool.Enter();
            using var b = ObjCAutoreleasePool.Enter();
        }
        Assert.Equal(new[] { "push 1", "push 2", "pop 2", "pop 1" }, native.Trace);
        Assert.Equal(new(2, 2, 0, 0), Delta(before));
    }

    [Fact]
    public void Enabled_frame_and_nested_explicit_scope_share_one_native_root()
    {
        using var native = new Native();
        var before = MetalFramePoolProbe.Counters;
        using (MetalFramePoolProbe.Enter(true))
        {
            using var a = ObjCAutoreleasePool.Enter();
            using (MetalFramePoolProbe.Enter(true))
            {
                using var b = ObjCAutoreleasePool.Enter();
            }
            using (MetalFramePoolProbe.Enter(false)) { using var disabled = ObjCAutoreleasePool.Enter(); }
            using var c = ObjCAutoreleasePool.Enter();
        }
        Assert.Equal(new[] { "push 1", "pop 1" }, native.Trace);
        Assert.Equal(new(1, 1, 4, 2), Delta(before));
        using (ObjCAutoreleasePool.Enter()) { }
        Assert.Equal("pop 2", native.Trace[^1]);
    }

    [Fact]
    public void Throwing_callback_preserves_primary_exception_and_following_frame_is_independent()
    {
        using var native = new Native();
        var primary = new InvalidOperationException("callback");
        var error = Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var frame = MetalFramePoolProbe.Enter(true);
            using var nested = MetalFramePoolProbe.Enter(true);
            using var local = ObjCAutoreleasePool.Enter();
            throw primary;
        }));
        Assert.Same(primary, error);
        using (MetalFramePoolProbe.Enter(true)) { using var local = ObjCAutoreleasePool.Enter(); }
        Assert.Equal(new[] { "push 1", "pop 1", "push 2", "pop 2" }, native.Trace);
    }

    [Fact]
    public void Root_is_inactive_before_native_pop_can_reenter_and_following_frame_works()
    {
        using var native = new Native();
        var before = MetalFramePoolProbe.Counters;
        native.OnPop = () => { using var local = ObjCAutoreleasePool.Enter(); };
        using (MetalFramePoolProbe.Enter(true)) { using var local = ObjCAutoreleasePool.Enter(); }
        native.OnPop = null;
        using (MetalFramePoolProbe.Enter(true)) { using var local = ObjCAutoreleasePool.Enter(); }
        Assert.Equal(new[] { "push 1", "pop 1", "push 2", "pop 2", "push 3", "pop 3" }, native.Trace);
        Assert.Equal(new(3, 3, 2, 2), Delta(before));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_push_never_publishes_active_state_and_counts_only_returned_native_calls(bool throws)
    {
        using var native = new Native { Fail = true, Throw = throws };
        var before = MetalFramePoolProbe.Counters;
        Assert.Throws<InvalidOperationException>(() => { using var scope = MetalFramePoolProbe.Enter(true); });
        Assert.Equal(new(throws ? 0 : 1, 0, 0, 0), Delta(before));
        native.Fail = false;
        using (ObjCAutoreleasePool.Enter()) { }
        using (MetalFramePoolProbe.Enter(true)) { using var local = ObjCAutoreleasePool.Enter(); }
        Assert.Equal(new(throws ? 2 : 3, 2, 1, 1), Delta(before));
    }

    [Fact]
    public void Out_of_order_disposal_is_refused_without_draining_a_live_nested_scope()
    {
        using var native = new Native();
        var outer = MetalFramePoolProbe.Enter(true);
        var inner = MetalFramePoolProbe.Enter(true);
        try
        {
            try { outer.Dispose(); Assert.Fail("out of order disposal was accepted"); }
            catch (InvalidOperationException) { }
            Assert.Equal(new[] { "push 1" }, native.Trace);
        }
        finally { inner.Dispose(); outer.Dispose(); }
        outer.Dispose();
        Assert.Equal(new[] { "push 1", "pop 1" }, native.Trace);
    }

    [Fact]
    public void Non_macOS_scopes_without_a_fake_are_no_ops()
    {
        if (OperatingSystem.IsMacOS()) return;
        var before = MetalFramePoolProbe.Counters;
        using (MetalFramePoolProbe.Enter(true)) { using var local = ObjCAutoreleasePool.Enter(); }
        Assert.Equal(before, MetalFramePoolProbe.Counters);
    }

    [Fact]
    public void Throwing_teardown_clears_active_state_before_the_following_frame()
    {
        using var native = new Native();
        native.OnPop = () => throw new InvalidOperationException("pop");
        Assert.Throws<InvalidOperationException>(() => { using var frame = MetalFramePoolProbe.Enter(true); });
        using (ObjCAutoreleasePool.Enter()) { }
        using (MetalFramePoolProbe.Enter(true)) { using var local = ObjCAutoreleasePool.Enter(); }
        Assert.Equal(new[] { "push 1", "pop 1", "push 2", "pop 2", "push 3", "pop 3" }, native.Trace);
    }

    [Fact]
    public void Active_state_is_not_published_while_native_push_is_still_running()
    {
        using var native = new Native();
        var before = MetalFramePoolProbe.Counters;
        native.OnPush = () => { using var local = ObjCAutoreleasePool.Enter(); };
        using (MetalFramePoolProbe.Enter(true)) { using var local = ObjCAutoreleasePool.Enter(); }
        Assert.Equal(new[] { "push 1", "push 2", "pop 2", "pop 1" }, native.Trace);
        Assert.Equal(new(2, 2, 1, 1), Delta(before));
    }

    static MetalFramePoolCounters Delta(MetalFramePoolCounters before)
    {
        var end = MetalFramePoolProbe.Counters;
        return new(end.NativePushCount - before.NativePushCount, end.NativePopCount - before.NativePopCount,
            end.BypassedLocalScopeCount - before.BypassedLocalScopeCount,
            end.ExplicitFrameScopeCount - before.ExplicitFrameScopeCount);
    }

    sealed class Native : IMetalPoolNative, IDisposable
    {
        int _next;
        readonly IMetalPoolNative? _previous = MetalPoolNative.Override;
        internal readonly List<string> Trace = new();
        internal bool Fail, Throw, Record = true;
        internal Action? OnPop, OnPush;
        internal Native() => MetalPoolNative.Override = this;
        public IntPtr Push()
        {
            if (Fail) return Throw ? throw new InvalidOperationException("push failed") : IntPtr.Zero;
            var id = new IntPtr(++_next);
            if (Record) Trace.Add("push " + id);
            var callback = OnPush;
            OnPush = null;
            callback?.Invoke();
            return id;
        }
        public void Pop(IntPtr pool)
        {
            if (Record) Trace.Add("pop " + pool);
            var callback = OnPop;
            OnPop = null;
            callback?.Invoke();
        }
        public void Dispose() => MetalPoolNative.Override = _previous;
    }
}
