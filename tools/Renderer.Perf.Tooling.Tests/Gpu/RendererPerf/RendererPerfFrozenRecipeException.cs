using System;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>A live recipe that does not match its frozen identity.</summary>
internal sealed class RendererPerfFrozenRecipeException(RendererPerfFrozenRecipeRefusal reason, string message)
    : InvalidOperationException(message)
{
    /// <summary>Why verification refused.</summary>
    internal RendererPerfFrozenRecipeRefusal Reason { get; } = reason;

    /// <summary>The recipe id.</summary>
    internal string? RecipeId { get; init; }

    /// <summary>The platform family verified against.</summary>
    internal string? Family { get; init; }

    /// <summary>The live derived digests a maintainer copies into the frozen recipe.</summary>
    internal RendererPerfDerivedDigests? ActualDerived { get; init; }
}
