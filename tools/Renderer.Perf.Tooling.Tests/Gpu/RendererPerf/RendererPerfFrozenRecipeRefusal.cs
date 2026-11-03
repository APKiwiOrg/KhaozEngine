namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Stable reasons frozen recipe verification refuses a live recipe.</summary>
internal enum RendererPerfFrozenRecipeRefusal
{
    /// <summary>The frozen recipe names another identity schema.</summary>
    UnsupportedSchema,
    /// <summary>The portable authored construction differs from the frozen one, on every platform family.</summary>
    AuthoredDiffers,
    /// <summary>The frozen recipe has no derived digests for the platform family.</summary>
    DerivedMissing,
    /// <summary>The derived digests differ from the frozen ones for the platform family.</summary>
    DerivedDiffers
}
