using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class RendererPerfMetalDiagnosticsTests
{
    [Fact]
    public void Device_orphan_route_uses_its_ledger_and_the_injected_production_owner()
    {
        var owner = typeof(MetalGpuDevice).GetMethod("CreateOrphanTarget", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var shared = typeof(MetalGpuDevice).GetMethod("CreateOrphanTarget", BindingFlags.NonPublic | BindingFlags.Static)!;
        var memory = typeof(MetalGpuDevice).GetProperty(nameof(MetalGpuDevice.Memory), BindingFlags.NonPublic | BindingFlags.Instance)!.GetMethod!;
        var calls = IlCallGraph.Callees(owner);
        Assert.Contains(memory, calls);
        Assert.Contains(shared, calls);
        Assert.True(Array.IndexOf(calls, memory) < Array.IndexOf(calls, shared));
        Assert.Contains(IlCallGraph.Callees(shared), method => method.DeclaringType == typeof(MetalTexture)
            && method.Name == nameof(MetalTexture.Create)
            && method.GetParameters()[0].ParameterType == typeof(IMetalResourceNative));
    }

    [Fact]
    public void Orphan_owner_accounts_creation_reuse_resize_and_drawable_adoption()
    {
        using var rig = new OrphanRig();
        Assert.Equal(0, rig.Api.AcquireCount);
        Assert.Empty(rig.Native.Created);
        Assert.Equal(0ul, rig.Memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
        rig.Bind();
        AssertOwnedOrphan(rig, 8294400, 8294400);
        IntPtr first = rig.Boundary.Framebuffer.Attachment.Texture;
        rig.Api.ScriptNoDrawable();
        rig.Boundary.Present();
        rig.Bind();
        AssertOwnedOrphan(rig, 8294400, 8294400);
        Assert.Equal(first, rig.Boundary.Framebuffer.Attachment.Texture);
        Assert.Single(rig.Native.Created);

        rig.Boundary.QueueResize(2560, 1440);
        rig.Api.ScriptNoDrawable();
        rig.Boundary.Present();
        rig.Bind();
        AssertOwnedOrphan(rig, 14745600, 14745600);
        Assert.Equal(1ul, rig.Memory.PeakResourceCount);
        Assert.Equal(first, Assert.Single(rig.Native.Released));
        Assert.Equal(2, rig.Native.Created.Count);
        Assert.Equal((2560u, 1440u), (rig.Native.Created[1].Width, rig.Native.Created[1].Height));
        IntPtr resizedOrphan = rig.Boundary.Framebuffer.Attachment.Texture;
        Assert.NotEqual(first, resizedOrphan);

        rig.Native.BeforeRelease = released =>
        {
            Assert.True(rig.Api.Handed[^1].HasDrawable);
            Assert.False(rig.Boundary.IsOrphanBound);
            IntPtr incomingDrawable = rig.Api.Handed[^1].Texture;
            Assert.Equal(resizedOrphan, released);
            Assert.NotEqual(resizedOrphan, incomingDrawable);
            Assert.Equal(incomingDrawable, rig.Boundary.Framebuffer.Attachment.Texture);
        };
        rig.Api.ScriptDrawable();
        rig.Boundary.Present();
        rig.Bind();
        Assert.Equal(0ul, rig.Memory.ReadMemorySnapshot().LogicalTextureBytes.Value!.UInt64);
        Assert.Equal(0ul, rig.Memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
        Assert.Equal(14745600ul, rig.Memory.ReadMemorySnapshot().PeakLogicalPayloadBytes.Value!.UInt64);
        Assert.False(rig.Orphan.IsLive);
        Assert.Equal(2, rig.Native.Released.Count);

        rig.Api.ScriptDrawable();
        rig.Boundary.Present();
        rig.Bind();
        Assert.Equal(0ul, rig.Memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
        Assert.Equal(0ul, rig.Memory.ReadMemorySnapshot().LogicalTextureBytes.Value!.UInt64);
        Assert.Equal(2, rig.Native.Created.Count);
        Assert.Equal(2, rig.Native.Released.Count);
        Assert.False(rig.Api.AcquireSawLockHeld);
        Assert.False(rig.Api.PresentBufferSawLockHeld);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Orphan_disposal_removes_ownership_once_and_preserves_dead_device_release_policy(bool dead)
    {
        using var rig = new OrphanRig();
        Assert.Equal(0, rig.Api.AcquireCount);
        Assert.Empty(rig.Native.Created);
        Assert.Equal(0ul, rig.Memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
        rig.Bind();
        AssertOwnedOrphan(rig, 8294400, 8294400);
        if (dead) rig.Liveness.MarkDead();
        rig.Boundary.Dispose();
        rig.Boundary.Dispose();
        rig.Orphan.Release();
        Assert.Equal(0ul, rig.Memory.ReadMemorySnapshot().LogicalTextureBytes.Value!.UInt64);
        Assert.Equal(0ul, rig.Memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
        Assert.Equal(8294400ul, rig.Memory.ReadMemorySnapshot().PeakLogicalPayloadBytes.Value!.UInt64);
        Assert.Equal(dead ? 0 : 1, rig.Native.Released.Count);
        Assert.Equal(1, rig.Api.Log.Count(entry => entry == "disposeApi"));
    }

    [Fact]
    public void Failed_orphan_replacement_releases_the_prior_owner_without_counting_failed_creation()
    {
        using var rig = new OrphanRig();
        Assert.Equal(0, rig.Api.AcquireCount);
        Assert.Empty(rig.Native.Created);
        Assert.Equal(0ul, rig.Memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
        rig.Bind();
        AssertOwnedOrphan(rig, 8294400, 8294400);
        rig.Native.RefuseTexture = true;
        rig.Boundary.QueueResize(2560, 1440);
        rig.Api.ScriptNoDrawable();
        rig.Boundary.Present();
        AssertOwnedOrphan(rig, 8294400, 8294400, bound: false);
        Assert.Throws<InvalidOperationException>(rig.Bind);
        Assert.Equal(0ul, rig.Memory.ReadMemorySnapshot().LogicalTextureBytes.Value!.UInt64);
        Assert.Equal(0ul, rig.Memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
        Assert.Equal(8294400ul, rig.Memory.ReadMemorySnapshot().PeakLogicalPayloadBytes.Value!.UInt64);
        Assert.False(rig.Orphan.IsLive);
        Assert.Single(rig.Native.Released);
    }

    static void AssertOwnedOrphan(OrphanRig rig, ulong bytes, ulong peak, bool bound = true)
    {
        var snapshot = rig.Memory.ReadMemorySnapshot();
        Assert.Equal(bytes, snapshot.LogicalTextureBytes.Value!.UInt64);
        Assert.Equal(1ul, snapshot.LiveResourceCount.Value!.UInt64);
        Assert.Equal(peak, snapshot.PeakLogicalPayloadBytes.Value!.UInt64);
        Assert.Equal(0ul, snapshot.LogicalBufferBytes.Value!.UInt64);
        Assert.Equal(0ul, snapshot.StagingCapacityBytes.Value!.UInt64);
        Assert.Equal(bound, rig.Boundary.IsOrphanBound);
    }

    sealed class OrphanRig : IDisposable
    {
        internal readonly MetalMemoryDiagnostics Memory = new();
        internal readonly DeviceLiveness Liveness = new();
        internal readonly FakeMetalSwapchainApi Api = new();
        internal readonly OrphanNative Native;
        internal readonly MetalOrphanTarget Orphan;
        internal readonly MetalPresentBoundary Boundary;

        internal OrphanRig()
        {
            var submitLock = new object();
            Api.LockToWatch = submitLock;
            Native = new(Memory, submitLock);
            Orphan = new((size, format) => MetalGpuDevice.CreateOrphanTarget(Native, Liveness, Memory, size, format));
            Api.ScriptNoDrawable();
            Boundary = new(Api, Orphan, new MetalUncommittedBuffers(3), new WaitAccumulator(), submitLock,
                () => { }, () => { }, new(1920, 1080), false, true, 3);
        }

        internal void Bind()
        {
            var source = (IMetalBoundFramebufferSource)Boundary.Framebuffer;
            int before = Api.AcquireCount;
            var first = source.AsBound;
            Assert.Equal(before + 1, Api.AcquireCount);
            Assert.Equal(first.Id, source.AsBound.Id);
            Assert.Equal(before + 1, Api.AcquireCount);
        }

        public void Dispose() => Boundary.Dispose();
    }

    sealed class OrphanNative(MetalMemoryDiagnostics memory, object submitLock) : IMetalResourceNative
    {
        // Owned textures use a separate range from FakeMetalSwapchainApi's borrowed handles.
        const int FirstOwnedTextureHandle = 0x100000;
        int _next = FirstOwnedTextureHandle;
        internal bool RefuseTexture;
        internal Action<IntPtr>? BeforeRelease;
        internal readonly List<GpuTextureDescription> Created = [];
        internal readonly List<IntPtr> Released = [];

        public MTLBuffer NewBuffer(nuint size) => throw new InvalidOperationException("An orphan must be a Private render-target texture.");
        public IntPtr Contents(MTLBuffer buffer) => throw new InvalidOperationException("An orphan must not be mapped.");
        public MTLTexture NewTexture(in GpuTextureDescription description, in MetalTextureViewPlan plan)
        {
            Assert.False(Monitor.IsEntered(submitLock));
            Assert.Equal(0ul, memory.ReadMemorySnapshot().LiveResourceCount.Value!.UInt64);
            Assert.Equal(GpuTextureUsage.RenderTarget, description.Usage);
            Assert.Equal(GpuPixelFormat.B8G8R8A8UNorm, description.Format);
            Assert.Equal(MTLStorageMode.Private, plan.Storage);
            if (RefuseTexture) return default;
            Created.Add(description);
            return new((IntPtr)_next++);
        }
        public void Release(IntPtr handle)
        {
            Assert.False(Monitor.IsEntered(submitLock));
            BeforeRelease?.Invoke(handle);
            Released.Add(handle);
        }
    }
}
