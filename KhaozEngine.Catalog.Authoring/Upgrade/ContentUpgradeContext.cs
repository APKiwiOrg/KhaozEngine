using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// Everything a planner is allowed to see: the version it is standing on, that version exported as a whole
/// bundle, its text included, and the frozen registry this build declares.
/// <para>
/// <b>The baseline is exported at the CURRENT active version, immediately before the definition runs.</b> A
/// run applying two definitions therefore hands the second one a bundle that already carries the first one's
/// published result, which is why a set is ordered and why <c>Preview</c> plans only the first pending
/// definition exactly.
/// </para>
/// <para>
/// A planner never touches the store. It gets the whole catalog as a value and returns a plan, which is what
/// makes the same context always plan the same way and a preview the same plan an apply carries out.
/// </para>
/// </summary>
public sealed class ContentUpgradeContext
{
    /// <summary>Builds the context the runner hands a planner.</summary>
    /// <param name="baselineVersion">The active version the definition is planning against, at least 1.</param>
    /// <param name="baseline">That version exported as a bundle, ids included.</param>
    /// <param name="registry">The registry this build declares, which the target bundle is checked against.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="baselineVersion"/> is not positive.</exception>
    /// <exception cref="ContentAuthoringException">The baseline is a format 2 bundle that lost its text section, or a format this build does not read.</exception>
    public ContentUpgradeContext(int baselineVersion, ContentBundle baseline, ContentTypeRegistry registry)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baselineVersion);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(registry);

        BaselineVersion = baselineVersion;
        Baseline = baseline;
        Registry = registry;
        BaselineText = ContentBundleTextCompatibility.TextOf(baseline, nameof(ContentUpgradeContext));
    }

    /// <summary>The active version the plan is built against, which is also the expected base of the publish.</summary>
    public int BaselineVersion { get; }

    /// <summary>The catalog as it stands: every live row with its id and key, the families and the rules.</summary>
    public ContentBundle Baseline { get; }

    /// <summary>The registry this build declares, which a committed target bundle has to agree with.</summary>
    public ContentTypeRegistry Registry { get; }

    /// <summary>
    /// The baseline's complete text: every declared language with its wire spelling, empty ones included, and
    /// every value. A format 1 baseline is text free by contract and reads as empty. A later planner sees the
    /// text an earlier definition in the same run published, because the baseline is exported after it.
    /// </summary>
    public ContentBundleTextState BaselineText { get; }
}
