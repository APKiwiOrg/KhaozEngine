using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class TestOwnedThreadsTests
{
    [Fact]
    public async Task Async_actor_flows_context_across_owned_continuations_without_changing_its_parent()
    {
        var value = new AsyncLocal<string?> { Value = "parent" };
        using var entered = new ControlledHold("The async actor did not enter.", TimeSpan.FromSeconds(10));
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task actor = DedicatedThread.Run(async () =>
        {
            Assert.False(Thread.CurrentThread.IsThreadPoolThread);
            Assert.Equal("parent", value.Value);
            value.Value = "child";
            entered.Release();
            await resume.Task;
            Assert.False(Thread.CurrentThread.IsThreadPoolThread);
            Assert.Equal("child", value.Value);
            await Task.Yield();
            Assert.False(Thread.CurrentThread.IsThreadPoolThread);
            Assert.Equal("child", value.Value);
        });
        Task producer = DedicatedThread.Run(() =>
        {
            entered.Hold();
            resume.SetResult();
        });

        Assert.Equal("parent", value.Value);
        await Task.WhenAll(actor, producer).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("parent", value.Value);
    }

    [Fact]
    public async Task Async_actor_reports_an_exception_after_yielding()
    {
        var expected = new InvalidOperationException("async actor failure");
        Task actor = DedicatedThread.Run(async () =>
        {
            await Task.Yield();
            throw expected;
        });

        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => actor.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(expected, actual);
    }
}
