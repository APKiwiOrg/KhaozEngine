using System;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("GpuBackendProvidersSerial")]
public sealed partial class RendererPerfColdLifecycleTests(ITestOutputHelper output)
{
    readonly ITestOutputHelper _output = output;
    [Fact]
    public void Budget_uses_original_origin_and_absolute_deadline()
    {
        long now = 150;
        using var cancellation = new CancellationTokenSource();
        var budget = new RendererPerfUnitBudget(new(() => now, 10), 100, 200, cancellation.Token);
        Assert.Equal(5000, budget.ElapsedMilliseconds());
        budget.Check("first");
        now = 200;
        Assert.Throws<TimeoutException>(() => budget.Check("second"));
        now = 150;
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => budget.Check("third"));
    }
}
