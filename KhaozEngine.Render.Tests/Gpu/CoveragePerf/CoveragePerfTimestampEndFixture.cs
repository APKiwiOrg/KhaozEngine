using System;
using System.Collections.Generic;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.CoveragePerf;

/// <summary>Real Vulkan list and pool lifecycles over the existing headless driver seams.</summary>
internal sealed class CoveragePerfTimestampEndFixture : IDisposable
{
    internal readonly VulkanResourceFixture Resources = new();
    internal readonly TimestampEndCommandApi Commands;
    internal readonly TimestampEndQueryApi Queries;
    internal readonly TimestampEndRenderApi Rendering;
    internal readonly VulkanCommandList List;
    internal readonly VulkanTimestampPool Pool;
    internal readonly Action<IGpuTimestampPool, uint> Arm;

    internal CoveragePerfTimestampEndFixture()
    {
        Commands = new(Resources.CommandApi, Resources.Trace);
        Queries = new(Resources.Trace, Commands);
        Rendering = new(Resources.RenderApi, Resources.Barriers, Commands);
        List = NewList();
        Pool = NewPool();
        Arm = Bind(List);
    }

    internal static Type Contract => Assert.IsAssignableFrom<Type>(
        typeof(IGpuCommandList).Assembly.GetType("KhaozEngine.Gpu.IGpuCommandListTimestampEnd"));

    // Reflection is cold-only, letting the red tests compile before the optional API exists.
    internal static Action<IGpuTimestampPool, uint> Bind(IGpuCommandList list)
    {
        Type contract = Contract;
        Assert.True(contract.IsInstanceOfType(list), "The Vulkan list must expose the completed-frame tail.");
        MethodInfo method = Assert.IsAssignableFrom<MethodInfo>(contract.GetMethod("FinishTimestampsOnEnd"));
        return method.CreateDelegate<Action<IGpuTimestampPool, uint>>(list);
    }

    internal VulkanCommandList NewList(IVulkanRecordUploads? uploads = null)
        => new(new VulkanCommandPoolRing(Commands, 3, Resources.Timeline, Resources.Backpressure),
            Resources.Retired, uploads, render: Rendering,
            layouts: new VulkanLayoutTracker(Rendering), timestampTimeline: Resources.Timeline);

    internal VulkanTimestampPool NewPool(uint capacity = 5)
        => new(Queries, Resources.Timeline, Resources.Owner, capacity, 64, 1);

    internal void Start(uint count = 3)
    {
        List.Begin();
        List.BeginTimestamps(Pool, count);
        for (uint i = 0; i + 1 < count; i++) List.WriteTimestamp(Pool, i);
    }

    public void Dispose()
    {
        List.Dispose();
        Pool.Dispose();
    }
}

/// <summary>Observes the existing native command boundary, with recording disabled for allocation windows.</summary>
internal sealed class TimestampEndCommandApi(FakeVulkanCommandApi inner, List<string> trace) : IVulkanCommandApi
{
    enum BufferState { Initial, Recording, Unusable }
    readonly Dictionary<ulong, ulong> _poolBuffers = new();
    readonly Dictionary<ulong, BufferState> _states = new();
    internal readonly InvalidOperationException Failure = new("Injected Vulkan finalization failure.");
    internal bool Record = true;
    internal bool FailRelease;
    internal bool FailEnd;
    internal Action? OnRelease;
    internal Action? OnEnd;
    internal int Submissions;
    internal int Releases;
    internal int Ends;

    public ulong CreatePool() => inner.CreatePool();
    public ulong AllocatePrimaryBuffer(ulong pool)
    {
        ulong buffer = inner.AllocatePrimaryBuffer(pool);
        _poolBuffers.Add(pool, buffer);
        _states.Add(buffer, BufferState.Initial);
        return buffer;
    }
    public void ResetPool(ulong pool)
    {
        if (Record) inner.ResetPool(pool);
        _states[_poolBuffers[pool]] = BufferState.Initial;
    }
    public void BeginOneTimeSubmit(ulong commandBuffer)
    {
        if (_states[commandBuffer] != BufferState.Initial)
            throw new InvalidOperationException("A command buffer must be pool-reset before another Begin.");
        if (Record) inner.BeginOneTimeSubmit(commandBuffer);
        _states[commandBuffer] = BufferState.Recording;
    }
    public void DestroyPool(ulong pool) => inner.DestroyPool(pool);

    internal void RequireRecording(ulong buffer)
    {
        if (_states[buffer] != BufferState.Recording)
            throw new InvalidOperationException("A command was recorded into an abandoned or sealed buffer before pool reset.");
    }

    internal void Abandon(ulong buffer) => _states[buffer] = BufferState.Unusable;

    internal void ThrowFailure(ulong buffer)
    {
        Abandon(buffer);
        throw Failure;
    }

    public void ReleaseMemoryForReuse(ulong commandBuffer)
    {
        RequireRecording(commandBuffer);
        Releases++;
        if (FailRelease) ThrowFailure(commandBuffer);
        if (Record)
        {
            inner.ReleaseMemoryForReuse(commandBuffer);
            trace.Add("ReleaseMemory");
        }
        OnRelease?.Invoke();
    }

    public void EndRecording(ulong commandBuffer)
    {
        RequireRecording(commandBuffer);
        Ends++;
        OnEnd?.Invoke();
        if (Record)
        {
            trace.Add("NativeEnd");
            inner.EndRecording(commandBuffer);
        }
        // A failed vkEndCommandBuffer has still consumed the native end attempt and invalidated the buffer.
        Abandon(commandBuffer);
        if (FailEnd) throw Failure;
    }

    public VulkanSubmitStatus Submit(ulong commandBuffer, ulong signalValue,
        in VulkanFrameSemaphores frame, out string? failure)
    {
        Submissions++;
        return inner.Submit(commandBuffer, signalValue, frame, out failure);
    }
}

internal sealed class TimestampEndQueryApi(List<string> trace, TimestampEndCommandApi commands) : IVulkanTimestampApi
{
    internal bool Record = true;
    internal bool FailWrite;
    internal int Writes;
    internal int Resets;
    internal int Creates;
    internal Action<ulong>? OnWrite;
    ulong _nextPool;

    public ulong CreatePool(uint capacity) { Creates++; return ++_nextPool; }
    public void DestroyPool(ulong pool) { }
    public void Reset(ulong commandBuffer, ulong pool, uint count)
    {
        commands.RequireRecording(commandBuffer);
        Resets++;
    }
    public void Write(ulong commandBuffer, ulong pool, uint index)
    {
        commands.RequireRecording(commandBuffer);
        if (FailWrite) commands.ThrowFailure(commandBuffer);
        Writes++;
        if (Record) trace.Add(index switch { 0 => "Write0", 1 => "Write1", _ => "Write2" });
        OnWrite?.Invoke(commandBuffer);
    }
    public bool TryRead(ulong pool, uint count, Span<ulong> values, Span<ulong> availability)
    {
        for (int i = 0; i < count; i++) values[i] = (ulong)i;
        availability.Fill(1);
        return true;
    }
}
