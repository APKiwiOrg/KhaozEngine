using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class GpuFrameSubmissionApiTests
{
    [Fact]
    public void Successful_retention_exposes_a_nonnull_completion_to_nullable_callers()
    {
        var completion = typeof(GpuFrameSubmission).GetMethod(nameof(GpuFrameSubmission.TryRetain))!.GetParameters()[1];
        var contract = completion.GetCustomAttribute<NotNullWhenAttribute>();
        Assert.NotNull(contract);
        Assert.True(contract.ReturnValue);
    }
}
