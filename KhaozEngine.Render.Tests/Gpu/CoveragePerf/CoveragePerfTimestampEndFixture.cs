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
    internal readonly VulkanCommandList List;
    internal readonly VulkanTimestampPool Pool;
    internal readonly Action<IGpuTimestampPool, uint> Arm;

    internal CoveragePerfTimestampEndFixture()
    {
        Commands = new(Resources.CommandApi, Resources.Trace);
        Queries = new(Resources.Trace);
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

    internal VulkanCommandList NewList()
        => new(new VulkanCommandPoolRing(Commands, 3, Resources.Timeline, Resources.Backpressure),
            Resources.Retired, render: Resources.RenderApi,
            layouts: new VulkanLayoutTracker(Resources.Barriers), timestampTimeline: Resources.Timeline);

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
    internal bool Record = true;
    internal bool FailRelease;
    internal bool FailEnd;
    internal Action? OnRelease;
    internal Action? OnEnd;
    internal int Submissions;
    internal int Releases;
    internal int Ends;

    public ulong CreatePool() => inner.CreatePool();
    public ulong AllocatePrimaryBuffer(ulong pool) => inner.AllocatePrimaryBuffer(pool);
    public void ResetPool(ulong pool) { if (Record) inner.ResetPool(pool); }
    public void BeginOneTimeSubmit(ulong commandBuffer) { if (Record) inner.BeginOneTimeSubmit(commandBuffer); }
    public void DestroyPool(ulong pool) => inner.DestroyPool(pool);

    public void ReleaseMemoryForReuse(ulong commandBuffer)
    {
        Releases++;
        if (FailRelease) throw new InvalidOperationException("Injected release failure.");
        if (Record)
        {
            inner.ReleaseMemoryForReuse(commandBuffer);
            trace.Add("ReleaseMemory");
        }
        OnRelease?.Invoke();
    }

    public void EndRecording(ulong commandBuffer)
    {
        Ends++;
        OnEnd?.Invoke();
        if (FailEnd) throw new InvalidOperationException("Injected native end failure.");
        if (Record)
        {
            trace.Add("NativeEnd");
            inner.EndRecording(commandBuffer);
        }
    }

    public VulkanSubmitStatus Submit(ulong commandBuffer, ulong signalValue,
        in VulkanFrameSemaphores frame, out string? failure)
    {
        Submissions++;
        return inner.Submit(commandBuffer, signalValue, frame, out failure);
    }
}

internal sealed class TimestampEndQueryApi(List<string> trace) : IVulkanTimestampApi
{
    internal bool Record = true;
    internal bool FailWrite;
    internal int Writes;
    internal int Resets;
    internal int Creates;
    ulong _nextPool;

    public ulong CreatePool(uint capacity) { Creates++; return ++_nextPool; }
    public void DestroyPool(ulong pool) { }
    public void Reset(ulong commandBuffer, ulong pool, uint count) => Resets++;
    public void Write(ulong commandBuffer, ulong pool, uint index)
    {
        if (FailWrite) throw new InvalidOperationException("Injected timestamp write failure.");
        Writes++;
        if (Record) trace.Add(index switch { 0 => "Write0", 1 => "Write1", _ => "Write2" });
    }
    public bool TryRead(ulong pool, uint count, Span<ulong> values, Span<ulong> availability)
    {
        for (int i = 0; i < count; i++) values[i] = (ulong)i;
        availability.Fill(1);
        return true;
    }
}
