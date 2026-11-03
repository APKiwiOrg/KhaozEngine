using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>A failed golden check with its evidence paths or caller-supplied bake refusal.</summary>
internal sealed record GoldenComparisonFailure(GoldenComparisonRefusal Reason, string Message)
{
    /// <summary>The captured frame's evidence PNG, when one was written.</summary>
    public string? GotPath { get; init; }
    /// <summary>The reference reconstructed as an evidence PNG, when one was written.</summary>
    public string? WantPath { get; init; }
    /// <summary>The difference heat map's evidence PNG, when one was written.</summary>
    public string? DiffPath { get; init; }
    /// <summary>The caller-supplied reason a bake could not proceed.</summary>
    public string? BakeRefusal { get; init; }

    /// <summary>Fails ordinary test callers with the same xUnit failure and diagnostic as before.</summary>
    [DoesNotReturn]
    internal void Throw() => Assert.Fail(Message);
}
