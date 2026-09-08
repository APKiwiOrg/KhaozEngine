using System;
using System.Threading.Tasks;
using Xunit;
using static KhaozEngine.Tests.WorldStore.Journal.MutationJournalExecutorTestSupport;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class JournalAsyncWaitTests
{
    [Fact]
    public async Task Wait_allows_an_asynchronous_condition_to_finish_after_a_short_delay()
    {
        Task completion = Task.Delay(TimeSpan.FromMilliseconds(250));

        await WaitUntilAsync(() => completion.IsCompleted);

        Assert.True(completion.IsCompleted);
    }

    [Fact]
    public async Task A_condition_that_never_finishes_still_fails_within_its_budget()
    {
        var failure = await Assert.ThrowsAsync<Xunit.Sdk.FailException>(
            () => WaitUntilAsync(() => false, TimeSpan.FromMilliseconds(20)));

        Assert.Contains("within", failure.Message, StringComparison.Ordinal);
    }
}
