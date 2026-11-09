using System;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Render3D;

/// <summary>Embedded compatibility contract shared by offline construction and runtime binding.
/// Binary provenance is evidence, never an arithmetic compatibility key.</summary>
internal static class CoverageArithmeticContract
{
    // Advance the version and descriptor before changing any retained output word, refusal boundary,
    // composition, refinement recipe, representative rule, transport or gather order. Add independent
    // frozen vectors for the new version. Keep the version-one position vectors exact when only direction arithmetic changes.
    internal const int Version = 2;
    internal const string Descriptor =
        "KhaozEngine.CoverageCarry/2\n" +
        "retained-scalar=binary32-rne,separate-operations,no-contraction,flush-operands-and-results-inclusive-2^-126,signed-zero\n" +
        "directions=native-binary32,no-software-flush,precise-cancellation-sensitive-operations\n" +
        "direction-recovery=local-only,finite-uncovered-then-authored-then-fixed-unitZ-normal-unitX-tangent,generated-first-source-corner,preserve-handedness-and-absence\n" +
        "direction-error=conditional-component<=2e-5,well-conditioned-forward-error-envelope,no-universal-bound,degenerate-band-finite-directions-and-identical-positions-only\n" +
        "palette=binary32-no-flush,inverse-bind-times-joint,left-associated-four-products\n" +
        "skin=weighted-four-bones,total<1e-8-identity,normal-linear-unit>1e-8,tangent-length2>1e-12\n" +
        "refinement=denominator12,ordered-source-edges,integer-weight-left-associated-sums,source-before-interpolation,alias-value-equality,direction-survival2^-50\n" +
        "representatives=first-active-vertex-per-position-group,retained-strength>0\n" +
        "transport=full-row-times-transition-unless-one,representative-position-minus-applied-times-current-direction,alias-position-sharing\n" +
        "basis=ordered-corners,positive-alignment-and-determinants,relative-determinant>1e-12\n" +
        "gather=normal-shading-corners,tangent-render-vertex-corners,unit-per-corner-then-ordered-sum-and-unit,preserve-handedness-and-absence\n" +
        "publication=hole-before,protected-before,atomic-on-position-failure-only,exact-position-record-preflight,nonfinite-position-unsupported-transform,position-alias-mismatch-folded-patch\n";

    internal static string Sha256 { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Descriptor)));
}
