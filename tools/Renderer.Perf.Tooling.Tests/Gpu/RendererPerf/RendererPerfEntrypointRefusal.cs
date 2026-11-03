namespace KhaozEngine.Tests.Gpu.RendererPerf;

/// <summary>Stable reasons the authorized entrypoint refuses before opening a native device. Every
/// <see cref="RendererPerfProtocolException"/> whose Code is "Entrypoint" carries one.</summary>
public enum RendererPerfEntrypointRefusal
{
    /// <summary>The process is not the first attempt of a GitHub-hosted run with native GPU tests enabled.</summary>
    NotFirstHostedAttempt,
    /// <summary>The unit or report directory passed to the entrypoint differs from the workflow's explicit inputs.</summary>
    InputsDisagree,
    /// <summary>The unit is not a safe lowercase filename token.</summary>
    UnsafeUnitToken,
    /// <summary><c>GITHUB_WORKSPACE</c> is not an absolute path.</summary>
    CheckoutRootNotAbsolute,
    /// <summary><c>GITHUB_WORKSPACE</c> is not the Git top level of its checkout.</summary>
    CheckoutRootNotGitTopLevel,
    /// <summary>The dispatched manifest or workload hash differs from the independent authorization facts.</summary>
    DispatchHashesDisagree,
    /// <summary>The checked-out head is not the authorization commit that adds only this unit's manifest to the
    /// immutable workload parent.</summary>
    AuthorizationNotManifestOnly,
    /// <summary>The authorized manifest names a different unit.</summary>
    ManifestUnitMismatch,
    /// <summary>The observed configuration, runtime, GC mode, tiering or relevant environment differs from the
    /// manifest's approved runtime.</summary>
    RuntimeDiffers,
    /// <summary>A recipe source path resolves outside the authorized checkout root.</summary>
    RecipeSourceOutsideCheckout
}
