namespace KhaozEngine.ItemInstances;

/// <summary>
/// Which set a guard belongs to, which spec 10.4 encodes as ONE empty reference on the
/// <c>currency_guard</c> row: a guard with no <c>currency_step_id</c> is a target guard and a guard naming
/// a step is a step guard.
/// <para>
/// <b>The difference is the whole reason both exist.</b> A target guard is a PRECONDITION on the craft and
/// failing one refuses everything and consumes nothing. A step guard is a condition on one step and failing
/// one SKIPS that step while the craft continues, which is how spec 10.4's whetstone repairs an item
/// already at quality 20 instead of refusing to touch it.
/// </para>
/// </summary>
public enum CraftGuardScope : byte
{
    /// <summary>Evaluated once, before any step, against the target.</summary>
    Target = 0,

    /// <summary>Evaluated before one step, and failing it skips only that step.</summary>
    Step = 1,
}

/// <summary>What a guard set answered, which is one of exactly three things.</summary>
public enum CraftGuardOutcome : byte
{
    /// <summary>Every guard held, so the craft or the step runs.</summary>
    Passed = 0,

    /// <summary>A step guard failed, so that ONE step does not run and the craft continues.</summary>
    Skipped = 1,

    /// <summary>A target guard failed, so the whole craft is refused and nothing is consumed.</summary>
    Refused = 2,
}

/// <summary>
/// One guard: a kind out of the closed fifteen plus at most two integer parameters, which is the whole of
/// spec 10.3's guard shape. The parameters' meaning is the <see cref="Kind"/>'s, and an unused one is 0.
/// <para>
/// <b>There is nowhere here for another guard to go</b>, which is what makes "no OR, no NOT and no nesting"
/// a property of the type rather than a rule someone has to remember. A currency that needs an OR is two
/// currency rows, which is one more authored row and no evaluator.
/// </para>
/// </summary>
/// <param name="Kind">Which question the guard asks.</param>
/// <param name="ParameterA">Its first parameter, or 0 when the kind takes none.</param>
/// <param name="ParameterB">Its second, or 0 when the kind takes one or none.</param>
public readonly record struct CraftGuard(CraftGuardKind Kind, int ParameterA, int ParameterB);
