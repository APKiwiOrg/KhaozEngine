using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog;

/// <summary>
/// The ONE validator (spec 5.1, contracts 10.4), shared by publish, server boot and every test. It takes
/// its whole world as arguments: a complete candidate, the previous published snapshot or null, the full
/// ordered rule set and the registry. Nothing is read from a database, a file or an ambient static.
/// <para>
/// It is PURE. No logging, no counters, no mutation of the candidate, and no throwing for a content
/// reason. A throw out of here is a bug in the validator, which is why the untrusted per-type validators
/// are wrapped and why every encode is guarded. The sweep ACCUMULATES, so one run reports every defect
/// rather than the earliest.
/// </para>
/// <para>
/// <b>The five passes run in order and never stop early</b> (spec 5.3). Structure, schema, references,
/// visibility and codec, then the remap rules. Pass 3 needs pass 1's walk over the live rows and pass 5
/// needs to know which ids ever existed, and nothing else is ordered. The passes are single threaded.
/// After them comes pass 6, Scope B's own band, and the per-type validators run LAST.
/// </para>
/// <para>
/// <b>What this validator deliberately does NOT check</b> (spec 5.5), named so nobody adds one later
/// without a decision:
/// </para>
/// <para>
/// <b>Three codes are ISSUED and are emitted somewhere other than this sweep</b>, named here because a
/// reader looking for one has to land somewhere that says where it lives, the way
/// <c>ContentKeyChecks.CheckFamilies</c> names its quiet four. <c>KEC0039</c> is a statement about a
/// ROLLBACK and <c>KEC0041</c> about a Fork EDIT (spec 5.3), both of which the authoring store emits
/// because only it holds the edit being made. <c>KEC0014</c> is a statement about the CLIENT-side encoded
/// bytes of a chunk (spec 6.7) and hangs off the client chunk encode, which has not shipped.
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Whether a value is sensible.</b> A sword worth 0 coins and a tree with a 100 percent chance are
///     legal. The validator enforces the SHAPE of content and the owner owns the numbers.
///   </description></item>
///   <item><description>
///     <b>Whether a client has the art.</b> The minimum client build is the publisher's statement about
///     that, and the validator cannot see a client's asset bundle.
///   </description></item>
///   <item><description>
///     <b>Whether a localization key resolves.</b> <c>IStringCatalog.Get</c> never throws for a missing key
///     and returns the key itself as a visible placeholder, so a missing string is a visible defect rather
///     than a publish blocker.
///   </description></item>
///   <item><description>
///     <b>Whether a remap rule is a GOOD idea.</b> <c>KEC0015</c> refuses a rule set that is not idempotent
///     and nothing refuses a rule that moves every sword onto a stick. That is the owner's call.
///   </description></item>
/// </list>
/// </summary>
public static class ContentValidator
{
    /// <summary>
    /// The one code that is informational rather than a failure: <c>previous</c> was null, so the
    /// publish-only checks did not run and a clean report from a boot is not a clean report from a publish.
    /// </summary>
    public const string InformationalCode = "KEC0000";

    /// <summary>A per-type validator's finding, or its throw, carried back under the engine's own code.</summary>
    public const string TypeValidatorCode = "KEC0040";

    /// <summary>The lowest code of Scope B's reserved band, which pass 6 emits into and nothing else does.</summary>
    public const int InstanceBandFirstCode = 100;

    /// <summary>The highest code of Scope B's reserved band.</summary>
    public const int InstanceBandLastCode = 199;

    const string PublishOnlyMessage =
        "previous is null, so the publish-only checks did not run: KEC0003 (a key changed on a published row), "
        + "KEC0029 (a type id, type key or chunk_slots changed after its first publish) and the item-instances "
        + "tier-ordinal check. A clean report from a boot is not a clean report from a publish.";

    /// <summary>
    /// Sweeps one candidate and reports every defect it carries.
    /// </summary>
    /// <param name="candidate">The complete candidate, which a publish builds and a boot decodes.</param>
    /// <param name="previous">
    /// The snapshot of the version the candidate is based on, or null. It is null at boot, null for a first
    /// publish and null in almost every test, and the three change-shaped checks are SKIPPED when it is,
    /// which is a property of the argument rather than a mode flag.
    /// </param>
    /// <param name="rules">The full ordered remap rule set, contracts 8.1.</param>
    /// <param name="registry">The registry the candidate's types are declared in.</param>
    /// <exception cref="ArgumentNullException">
    /// An argument other than <paramref name="previous"/> is null, which is a programming error rather than
    /// a content defect and so is the one thing here that throws.
    /// </exception>
    public static ContentValidationReport Validate(
        ContentSnapshot candidate,
        ContentSnapshot? previous,
        IReadOnlyList<RemapRule> rules,
        ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(registry);

        var run = new ContentValidationRun(candidate, previous, rules, registry);
        if (previous is null)
        {
            run.Add(default, 0, InformationalCode, PublishOnlyMessage);
        }

        ContentKeyChecks.Run(run);
        ContentSchemaChecks.Run(run);
        ContentReferenceChecks.Run(run);
        ContentVisibilityChecks.Run(run);
        ContentRemapChecks.Run(run);
        RunInstanceBand(run);
        RunTypeValidators(run);

        bool valid = true;
        foreach (ContentFinding finding in run.Findings)
        {
            if (!string.Equals(finding.Code, InformationalCode, StringComparison.Ordinal))
            {
                valid = false;
                break;
            }
        }

        return new ContentValidationReport(valid, run.Findings);
    }

    /// <summary>
    /// Pass 6, Scope B's band, which is INSIDE the sweep rather than in the per-type slot (spec 5.3). Its
    /// types are engine code in the engine's own 256 to 1023 band, so their checks are the engine's checks:
    /// a band registration's own <see cref="IContentValidator"/> runs HERE, its findings are added
    /// UNCHANGED rather than folded into <c>KEC0040</c>, and a throw from one PROPAGATES rather than
    /// becoming a finding, because a throw in the engine's own band is a bug and not untrusted input.
    /// <para>
    /// <b>The band is handed the run's OWN <c>previous</c> and rule set, through
    /// <see cref="IContentHistoryValidator"/>.</b> A band validator that implements it is reached through
    /// that overload and a band validator that does not is reached through the plain one, which is the
    /// whole of the difference. Without the overload the band saw the null a boot sees even on a publish
    /// that held a previous version, so every change-shaped check of the band was dead code and nothing in
    /// the report said so (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/962">962</see>). That
    /// is the one thing this pass does that <see cref="RunTypeValidators"/> deliberately does not: a game
    /// validator is untrusted and never sees the previous version.
    /// </para>
    /// <para>
    /// <b>The band cannot be named from here and that is the whole shape of this pass.</b>
    /// <c>KhaozEngine.Catalog</c> cannot reference the package that registers the band without closing a
    /// cycle, so the band arrives through the registration it already carries and the codes it emits,
    /// <c>KEC0100</c> to <c>KEC0199</c>, are reserved so an operator can tell them apart from this
    /// package's own.
    /// </para>
    /// <para>
    /// A band type does NOT run again in <see cref="RunTypeValidators"/>. Pass 6 owns it, so its finding
    /// reaches the report once and under its own token rather than twice under two.
    /// </para>
    /// <para>
    /// Two skips keep the pass at nothing. A process that registers no band type has none to walk, which is
    /// every boot of a game that does not use instances, and a band registration that declares no validator
    /// is swept by passes 1 to 5 like any other type and adds nothing of its own.
    /// </para>
    /// </summary>
    static void RunInstanceBand(ContentValidationRun run)
    {
        foreach (ContentTypeRegistration registration in run.Registry.ByTypeId)
        {
            if (!registration.Type.IsInstances)
            {
                continue;
            }

            IContentValidator? validator = registration.Validator;
            if (validator is null)
            {
                continue;
            }

            var own = new List<ContentFinding>();
            if (validator is IContentHistoryValidator history)
            {
                history.Validate(run.Candidate, run.Previous, run.Rules, own);
            }
            else
            {
                validator.Validate(registration.Type, run.Candidate, own);
            }

            foreach (ContentFinding finding in own)
            {
                run.Findings.Add(finding);
            }
        }
    }

    /// <summary>
    /// The per-type validators (contracts 4.4), LAST, after pass 6, one per registered type. Each is handed
    /// its own type id and a read-only view of the whole candidate, and each may only ADD a constraint.
    /// <para>
    /// A validator is UNTRUSTED code, so a throw out of one becomes a single <c>KEC0040</c> carrying the
    /// exception message after any findings it added before throwing have been wrapped and kept. One bad
    /// validator must not take a publish down with a stack trace where a finding was expected.
    /// </para>
    /// <para>
    /// The item-instances band is SKIPPED here, because pass 6 already ran it as trusted engine code. That
    /// is the one place the two loops differ, and it is what keeps a band finding under its own code.
    /// </para>
    /// <para>
    /// The finding comes back under <c>KEC0040</c> with the game's own message, prefixed by its type key, so
    /// an operator can key a runbook on a code that never changes and still see which type produced it.
    /// Spec 5.2 words this as the code carrying the prefix, which would make the token unstable, so the
    /// prefix goes on the message and the code stays exactly <c>KEC0040</c>.
    /// </para>
    /// </summary>
    static void RunTypeValidators(ContentValidationRun run)
    {
        foreach (ContentTypeRegistration registration in run.Registry.ByTypeId)
        {
            // A band type already ran, TRUSTED, in pass 6. Running it again here would report every
            // KEC0100-band finding a second time under KEC0040, which says the opposite about where the
            // finding came from.
            if (registration.Type.IsInstances)
            {
                continue;
            }

            IContentValidator? validator = registration.Validator;
            if (validator is null)
            {
                continue;
            }

            var own = new List<ContentFinding>();
            Exception? failure = null;
            try
            {
                validator.Validate(registration.Type, run.Candidate, own);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            foreach (ContentFinding finding in own)
            {
                run.Add(
                    finding.Type,
                    finding.Id,
                    TypeValidatorCode,
                    FormattableString.Invariant($"{registration.TypeKey}: {finding.Code} {finding.Message}"));
            }

            if (failure is not null)
            {
                run.Add(
                    registration.Type,
                    0,
                    TypeValidatorCode,
                    FormattableString.Invariant(
                        $"{registration.TypeKey}: the type's own validator threw {failure.GetType().Name}, '{failure.Message}'. A validator is untrusted code, so its throw is a finding rather than a failed publish."));
            }
        }
    }
}

/// <summary>
/// One sweep's world and its accumulating finding list, handed to each pass in turn. It exists so
/// <see cref="ContentValidator"/> holds the sweep and nothing else, and so adding a check grows a pass file
/// rather than the sweep (spec 2.7).
/// </summary>
internal sealed class ContentValidationRun
{
    internal ContentValidationRun(
        ContentSnapshot candidate,
        ContentSnapshot? previous,
        IReadOnlyList<RemapRule> rules,
        ContentTypeRegistry registry)
    {
        Candidate = candidate;
        Previous = previous;
        Rules = rules;
        Registry = registry;
        Findings = [];
    }

    /// <summary>The candidate under sweep.</summary>
    internal ContentSnapshot Candidate { get; }

    /// <summary>The previous published snapshot, or null, which is what the change-shaped checks need.</summary>
    internal ContentSnapshot? Previous { get; }

    /// <summary>The full ordered rule set, which pass 5 walks.</summary>
    internal IReadOnlyList<RemapRule> Rules { get; }

    /// <summary>The registry every type declaration is read from.</summary>
    internal ContentTypeRegistry Registry { get; }

    /// <summary>The findings so far, in emit order.</summary>
    internal List<ContentFinding> Findings { get; }

    /// <summary>Records one finding. There is no early exit anywhere: a pass runs to its end.</summary>
    internal void Add(ContentTypeId type, int id, string code, string message)
        => Findings.Add(new ContentFinding(type, id, code, message));

    /// <summary>
    /// One type's registration by its stable key, which is how the handful of ENGINE-SPECIFIC checks find
    /// their type. By key rather than by id, so a check cannot fire against a foreign type that happens to
    /// hold the engine's number.
    /// </summary>
    internal bool TryGetEngineType(string typeKey, [MaybeNullWhen(false)] out ContentTypeRegistration registration)
        => Registry.TryGetByKey(typeKey, out registration);

    /// <summary>The index of a schema field by name, which is its index in every row, or -1.</summary>
    internal static int FieldIndex(ContentFieldSchema schema, string name) => schema.IndexOf(name);

    /// <summary>
    /// One field's value on a row, ABSENT when the row is shorter than the schema. A short row is a
    /// <c>KEC0005</c> for every required field it dropped, reported by the schema pass, and every other
    /// check reads it as absent rather than walking off the end.
    /// </summary>
    internal static ContentFieldValue Value(ContentRow row, int index, ContentFieldKind kind)
        => index >= 0 && index < row.Fields.Count ? row.Fields[index] : ContentFieldValue.Absent(kind);
}
